using DeveMobileLPR.Inference.Cct;
using Google.AI.Edge.LiteRT;

namespace DeveMobileLPR.App.Platforms.Android.Inference;

/// <summary>
/// Executes the fixed-output CCT-S V2 OCR graph through Android LiteRT. NPU,
/// GPU, and CPU are each proven with a complete warm run before selection.
/// </summary>
internal sealed class AndroidLiteRtCctRawModel : ICctRawModel
{
    private const int PlateOutputValueCount = 10 * 37;
    private const int RegionOutputValueCount = 66;
    private readonly Session _session;
    private bool _disposed;

    public AndroidLiteRtCctRawModel(
        string modelPath,
        Action<string>? diagnostic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("LiteRT OCR model is missing.", modelPath);
        }

        _session = TryCreate(modelPath, Accelerator.Npu, "NPU", diagnostic)
            ?? TryCreate(modelPath, Accelerator.Gpu, "GPU", diagnostic)
            ?? TryCreate(modelPath, Accelerator.Cpu, "CPU", diagnostic)
            ?? throw new InvalidOperationException("LiteRT could not initialize OCR on NPU, GPU, or CPU.");
        BackendName = $"LiteRT {_session.AcceleratorName}";
    }

    public string BackendName { get; }

    public CctRawModelOutput Run(byte[] input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length != CctPlateRecognizer.InputValueCount)
        {
            throw new ArgumentException($"Expected {CctPlateRecognizer.InputValueCount} OCR input values.", nameof(input));
        }

        return _session.Run(input);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
    }

    private static Session? TryCreate(
        string modelPath,
        Accelerator? accelerator,
        string acceleratorName,
        Action<string>? diagnostic)
    {
        if (accelerator is null)
        {
            diagnostic?.Invoke($"LiteRT OCR {acceleratorName} accelerator is unavailable in the binding.");
            return null;
        }

        try
        {
            var session = Session.Create(modelPath, accelerator, acceleratorName);
            diagnostic?.Invoke($"LiteRT OCR selected {acceleratorName}; a full warm inference completed successfully.");
            return session;
        }
        catch (Exception exception)
        {
            diagnostic?.Invoke($"LiteRT OCR rejected {acceleratorName}: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly CompiledModel _model;
        private readonly IList<TensorBuffer> _inputs;
        private readonly IList<TensorBuffer> _outputs;
        private readonly int _plateOutputIndex;
        private readonly int _regionOutputIndex;
        private bool _disposed;

        private Session(
            CompiledModel model,
            IList<TensorBuffer> inputs,
            IList<TensorBuffer> outputs,
            int plateOutputIndex,
            int regionOutputIndex,
            string acceleratorName)
        {
            _model = model;
            _inputs = inputs;
            _outputs = outputs;
            _plateOutputIndex = plateOutputIndex;
            _regionOutputIndex = regionOutputIndex;
            AcceleratorName = acceleratorName;
        }

        public string AcceleratorName { get; }

        public static Session Create(
            string modelPath,
            Accelerator accelerator,
            string acceleratorName)
        {
            CompiledModel? model = null;
            IList<TensorBuffer>? inputs = null;
            IList<TensorBuffer>? outputs = null;
            try
            {
                using var options = new CompiledModel.Options(accelerator);
                model = CompiledModel.Create(modelPath, options);
                inputs = model.CreateInputBuffers();
                outputs = model.CreateOutputBuffers();
                if (inputs.Count != 1 || outputs.Count != 2)
                {
                    throw new InvalidDataException(
                        $"Expected one LiteRT OCR input and two outputs, got {inputs.Count} and {outputs.Count}.");
                }

                inputs[0].WriteInt8(new byte[CctPlateRecognizer.InputValueCount]);
                model.Run(inputs, outputs);
                var firstOutput = outputs[0].ReadFloat();
                var secondOutput = outputs[1].ReadFloat();
                var (plateOutputIndex, regionOutputIndex) = IdentifyOutputs(firstOutput, secondOutput);

                var session = new Session(
                    model,
                    inputs,
                    outputs,
                    plateOutputIndex,
                    regionOutputIndex,
                    acceleratorName);
                model = null;
                inputs = null;
                outputs = null;
                return session;
            }
            finally
            {
                DisposeBuffers(inputs);
                DisposeBuffers(outputs);
                model?.Dispose();
            }
        }

        public CctRawModelOutput Run(byte[] input)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _inputs[0].WriteInt8(input);
            _model.Run(_inputs, _outputs);
            return new CctRawModelOutput(
                _outputs[_plateOutputIndex].ReadFloat(),
                _outputs[_regionOutputIndex].ReadFloat());
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeBuffers(_inputs);
            DisposeBuffers(_outputs);
            _model.Dispose();
        }

        private static (int Plate, int Region) IdentifyOutputs(float[] first, float[] second)
        {
            if (first.Length == PlateOutputValueCount && second.Length == RegionOutputValueCount)
            {
                return (0, 1);
            }

            if (second.Length == PlateOutputValueCount && first.Length == RegionOutputValueCount)
            {
                return (1, 0);
            }

            throw new InvalidDataException(
                $"Unexpected LiteRT OCR output lengths {first.Length} and {second.Length}; "
                + $"expected {PlateOutputValueCount} plate values and {RegionOutputValueCount} region values.");
        }

        private static void DisposeBuffers(IList<TensorBuffer>? buffers)
        {
            if (buffers is null)
            {
                return;
            }

            foreach (var buffer in buffers)
            {
                buffer.Dispose();
            }

            (buffers as IDisposable)?.Dispose();
        }
    }
}
