using System.Runtime.InteropServices;

namespace DeveMobileLPR.App.Services;

/// <summary>
/// Owns a LiteRT C interpreter and, when requested, its Metal delegate. The
/// delegate is accepted only after allocation, tensor-contract validation, and
/// a complete warm inference have all succeeded.
/// </summary>
internal sealed class IosLiteRtSession : IDisposable
{
    private IntPtr _model;
    private IntPtr _interpreter;
    private IntPtr _delegate;
    private bool _disposed;

    private IosLiteRtSession(IntPtr model, IntPtr interpreter, IntPtr delegateHandle, string acceleratorName)
    {
        _model = model;
        _interpreter = interpreter;
        _delegate = delegateHandle;
        AcceleratorName = acceleratorName;
    }

    public string AcceleratorName { get; }

    public static IosLiteRtSession Create(
        string modelPath,
        bool useMetal,
        TfLiteTensorContract input,
        IReadOnlyList<TfLiteTensorContract> outputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(outputs);

        IntPtr model = IntPtr.Zero;
        IntPtr options = IntPtr.Zero;
        IntPtr interpreter = IntPtr.Zero;
        IntPtr delegateHandle = IntPtr.Zero;
        try
        {
            model = Native.TfLiteModelCreateFromFile(modelPath);
            if (model == IntPtr.Zero)
            {
                throw new InvalidDataException($"LiteRT could not load model '{Path.GetFileName(modelPath)}'.");
            }

            options = Native.TfLiteInterpreterOptionsCreate();
            if (options == IntPtr.Zero)
            {
                throw new InvalidOperationException("LiteRT could not create interpreter options.");
            }

            Native.TfLiteInterpreterOptionsSetNumThreads(
                options,
                Math.Clamp(Environment.ProcessorCount - 1, 1, 4));
            if (useMetal)
            {
                delegateHandle = Native.TFLGpuDelegateCreate(IntPtr.Zero);
                if (delegateHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("LiteRT could not create the Metal delegate.");
                }

                Native.TfLiteInterpreterOptionsAddDelegate(options, delegateHandle);
            }

            interpreter = Native.TfLiteInterpreterCreate(model, options);
            if (interpreter == IntPtr.Zero)
            {
                throw new InvalidOperationException("LiteRT could not create the interpreter.");
            }

            EnsureSuccess(
                Native.TfLiteInterpreterAllocateTensors(interpreter),
                "allocate model tensors");
            ValidateContract(interpreter, input, outputs);

            var session = new IosLiteRtSession(
                model,
                interpreter,
                delegateHandle,
                useMetal ? "Metal GPU" : "CPU");
            model = IntPtr.Zero;
            interpreter = IntPtr.Zero;
            delegateHandle = IntPtr.Zero;
            return session;
        }
        finally
        {
            if (options != IntPtr.Zero)
            {
                Native.TfLiteInterpreterOptionsDelete(options);
            }
            if (interpreter != IntPtr.Zero)
            {
                Native.TfLiteInterpreterDelete(interpreter);
            }
            if (delegateHandle != IntPtr.Zero)
            {
                Native.TFLGpuDelegateDelete(delegateHandle);
            }
            if (model != IntPtr.Zero)
            {
                Native.TfLiteModelDelete(model);
            }
        }
    }

    public void CopyInput(float[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        CopyInput(input, checked((nuint)(input.Length * sizeof(float))));
    }

    public void CopyInput(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        CopyInput(input, checked((nuint)input.Length));
    }

    public void Invoke()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureSuccess(Native.TfLiteInterpreterInvoke(_interpreter), "run inference");
    }

    public float[] ReadFloatOutput(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tensor = GetOutput(index);
        if (Native.TfLiteTensorType(tensor) != TfLiteType.Float32)
        {
            throw new InvalidDataException($"LiteRT output {index} is not float32.");
        }

        var byteSize = Native.TfLiteTensorByteSize(tensor);
        if (byteSize % sizeof(float) != 0)
        {
            throw new InvalidDataException($"LiteRT output {index} has an invalid byte length {byteSize}.");
        }

        var output = new float[checked((int)(byteSize / sizeof(float)))];
        var pinned = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            EnsureSuccess(
                Native.TfLiteTensorCopyToBuffer(tensor, pinned.AddrOfPinnedObject(), byteSize),
                $"copy output {index}");
        }
        finally
        {
            pinned.Free();
        }

        return output;
    }

    private void CopyInput(Array input, nuint byteSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tensor = Native.TfLiteInterpreterGetInputTensor(_interpreter, 0);
        if (tensor == IntPtr.Zero)
        {
            throw new InvalidOperationException("LiteRT returned no input tensor.");
        }

        var expected = Native.TfLiteTensorByteSize(tensor);
        if (expected != byteSize)
        {
            throw new ArgumentException($"Expected {expected} input bytes, received {byteSize}.", nameof(input));
        }

        var pinned = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            EnsureSuccess(
                Native.TfLiteTensorCopyFromBuffer(tensor, pinned.AddrOfPinnedObject(), byteSize),
                "copy input");
        }
        finally
        {
            pinned.Free();
        }
    }

    private static void ValidateContract(
        IntPtr interpreter,
        TfLiteTensorContract input,
        IReadOnlyList<TfLiteTensorContract> outputs)
    {
        var inputCount = Native.TfLiteInterpreterGetInputTensorCount(interpreter);
        var outputCount = Native.TfLiteInterpreterGetOutputTensorCount(interpreter);
        if (inputCount != 1 || outputCount != outputs.Count)
        {
            throw new InvalidDataException(
                $"Unexpected LiteRT tensor counts: {inputCount} inputs and {outputCount} outputs.");
        }

        ValidateTensor(
            Native.TfLiteInterpreterGetInputTensor(interpreter, 0),
            input,
            "input 0");
        var unmatched = outputs.ToList();
        for (var index = 0; index < outputs.Count; index++)
        {
            var tensor = Native.TfLiteInterpreterGetOutputTensor(interpreter, index);
            var match = unmatched.FindIndex(contract => TensorMatches(tensor, contract));
            if (match < 0)
            {
                var actualType = tensor == IntPtr.Zero ? TfLiteType.None : Native.TfLiteTensorType(tensor);
                var actualShape = tensor == IntPtr.Zero ? [] : ReadShape(tensor);
                throw new InvalidDataException(
                    $"Unexpected LiteRT output {index}: {actualType} [{string.Join(',', actualShape)}].");
            }
            unmatched.RemoveAt(match);
        }
    }

    private static bool TensorMatches(IntPtr tensor, TfLiteTensorContract contract) =>
        tensor != IntPtr.Zero
        && Native.TfLiteTensorType(tensor) == contract.Type
        && ReadShape(tensor).SequenceEqual(contract.Shape);

    private static void ValidateTensor(IntPtr tensor, TfLiteTensorContract contract, string name)
    {
        if (tensor == IntPtr.Zero)
        {
            throw new InvalidDataException($"LiteRT returned no {name} tensor.");
        }

        var actualType = Native.TfLiteTensorType(tensor);
        var actualShape = ReadShape(tensor);
        if (actualType != contract.Type || !actualShape.SequenceEqual(contract.Shape))
        {
            throw new InvalidDataException(
                $"Unexpected LiteRT {name}: {actualType} [{string.Join(',', actualShape)}]; "
                + $"expected {contract.Type} [{string.Join(',', contract.Shape)}].");
        }
    }

    private static int[] ReadShape(IntPtr tensor)
    {
        var count = Native.TfLiteTensorNumDims(tensor);
        if (count < 0)
        {
            throw new InvalidDataException("LiteRT returned a tensor without a fixed shape.");
        }

        var shape = new int[count];
        for (var index = 0; index < count; index++)
        {
            shape[index] = Native.TfLiteTensorDim(tensor, index);
        }
        return shape;
    }

    private IntPtr GetOutput(int index)
    {
        if (index < 0 || index >= Native.TfLiteInterpreterGetOutputTensorCount(_interpreter))
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return Native.TfLiteInterpreterGetOutputTensor(_interpreter, index);
    }

    private static void EnsureSuccess(TfLiteStatus status, string operation)
    {
        if (status != TfLiteStatus.Ok)
        {
            throw new InvalidOperationException($"LiteRT failed to {operation} ({status}).");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_interpreter != IntPtr.Zero)
        {
            Native.TfLiteInterpreterDelete(_interpreter);
            _interpreter = IntPtr.Zero;
        }
        if (_delegate != IntPtr.Zero)
        {
            Native.TFLGpuDelegateDelete(_delegate);
            _delegate = IntPtr.Zero;
        }
        if (_model != IntPtr.Zero)
        {
            Native.TfLiteModelDelete(_model);
            _model = IntPtr.Zero;
        }
    }

    private static class Native
    {
        private const string Library = "__Internal";

        [DllImport(Library, CharSet = CharSet.Ansi)]
        internal static extern IntPtr TfLiteModelCreateFromFile(string modelPath);

        [DllImport(Library)]
        internal static extern void TfLiteModelDelete(IntPtr model);

        [DllImport(Library)]
        internal static extern IntPtr TfLiteInterpreterOptionsCreate();

        [DllImport(Library)]
        internal static extern void TfLiteInterpreterOptionsDelete(IntPtr options);

        [DllImport(Library)]
        internal static extern void TfLiteInterpreterOptionsSetNumThreads(IntPtr options, int threadCount);

        [DllImport(Library)]
        internal static extern void TfLiteInterpreterOptionsAddDelegate(IntPtr options, IntPtr delegateHandle);

        [DllImport(Library)]
        internal static extern IntPtr TfLiteInterpreterCreate(IntPtr model, IntPtr options);

        [DllImport(Library)]
        internal static extern void TfLiteInterpreterDelete(IntPtr interpreter);

        [DllImport(Library)]
        internal static extern TfLiteStatus TfLiteInterpreterAllocateTensors(IntPtr interpreter);

        [DllImport(Library)]
        internal static extern TfLiteStatus TfLiteInterpreterInvoke(IntPtr interpreter);

        [DllImport(Library)]
        internal static extern int TfLiteInterpreterGetInputTensorCount(IntPtr interpreter);

        [DllImport(Library)]
        internal static extern int TfLiteInterpreterGetOutputTensorCount(IntPtr interpreter);

        [DllImport(Library)]
        internal static extern IntPtr TfLiteInterpreterGetInputTensor(IntPtr interpreter, int index);

        [DllImport(Library)]
        internal static extern IntPtr TfLiteInterpreterGetOutputTensor(IntPtr interpreter, int index);

        [DllImport(Library)]
        internal static extern TfLiteType TfLiteTensorType(IntPtr tensor);

        [DllImport(Library)]
        internal static extern int TfLiteTensorNumDims(IntPtr tensor);

        [DllImport(Library)]
        internal static extern int TfLiteTensorDim(IntPtr tensor, int index);

        [DllImport(Library)]
        internal static extern nuint TfLiteTensorByteSize(IntPtr tensor);

        [DllImport(Library)]
        internal static extern TfLiteStatus TfLiteTensorCopyFromBuffer(IntPtr tensor, IntPtr input, nuint byteSize);

        [DllImport(Library)]
        internal static extern TfLiteStatus TfLiteTensorCopyToBuffer(IntPtr tensor, IntPtr output, nuint byteSize);

        [DllImport(Library)]
        internal static extern IntPtr TFLGpuDelegateCreate(IntPtr options);

        [DllImport(Library)]
        internal static extern void TFLGpuDelegateDelete(IntPtr delegateHandle);
    }
}

internal sealed record TfLiteTensorContract(TfLiteType Type, params int[] Shape);

internal enum TfLiteStatus
{
    Ok = 0,
    Error = 1,
    DelegateError = 2,
    ApplicationError = 3,
    DelegateDataNotFound = 4,
    DelegateDataWriteError = 5,
    DelegateDataReadError = 6,
    UnresolvedOps = 7,
    Cancelled = 8,
    OutputShapeNotKnown = 9
}

internal enum TfLiteType
{
    None = 0,
    Float32 = 1,
    Int32 = 2,
    UInt8 = 3,
    Int64 = 4,
    String = 5,
    Bool = 6,
    Int16 = 7,
    Complex64 = 8,
    Int8 = 9,
    Float16 = 10,
    Float64 = 11,
    Complex128 = 12,
    UInt64 = 13,
    Resource = 14,
    Variant = 15,
    UInt32 = 16,
    UInt16 = 17,
    Int4 = 18,
    BFloat16 = 19
}
