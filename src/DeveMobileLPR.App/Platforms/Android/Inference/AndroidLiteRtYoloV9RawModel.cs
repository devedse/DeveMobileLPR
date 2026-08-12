using DeveMobileLPR.Inference.Yolo;
using Google.AI.Edge.LiteRT;

namespace DeveMobileLPR.App.Platforms.Android.Inference;

/// <summary>
/// Executes only the fixed-output YOLOv9 graph through Android LiteRT. GPU is
/// proven with a complete warm run before it is selected; CPU is the explicit
/// fallback rather than an invisible per-operation fallback.
/// </summary>
internal sealed class AndroidLiteRtYoloV9RawModel : IYoloV9RawModel
{
    private const int CandidateCount = 7_581;
    private const int BoxCoordinateCount = 4;
    private readonly Session _session;
    private bool _disposed;

    public AndroidLiteRtYoloV9RawModel(
        string modelPath,
        Action<string>? diagnostic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("LiteRT detector model is missing.", modelPath);
        }

        _session = TryCreate(modelPath, Accelerator.Gpu, "GPU", diagnostic)
            ?? TryCreate(modelPath, Accelerator.Cpu, "CPU", diagnostic)
            ?? throw new InvalidOperationException("LiteRT could not initialize the detector on GPU or CPU.");
        BackendName = $"LiteRT {_session.AcceleratorName}";
    }

    public string BackendName { get; }
    public YoloV9InputLayout InputLayout => YoloV9InputLayout.ChannelsLast;

    public YoloV9RawModelOutput Run(float[] input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        var expectedInputLength = YoloV9RawPlateDetector.InputValueCount;
        if (input.Length != expectedInputLength)
        {
            throw new ArgumentException($"Expected {expectedInputLength} detector input values.", nameof(input));
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
            diagnostic?.Invoke($"LiteRT {acceleratorName} accelerator is unavailable in the binding.");
            return null;
        }

        try
        {
            var session = Session.Create(modelPath, accelerator, acceleratorName);
            diagnostic?.Invoke(
                $"LiteRT detector selected {acceleratorName}; a full warm inference completed successfully.");
            return session;
        }
        catch (Exception exception)
        {
            diagnostic?.Invoke(
                $"LiteRT detector rejected {acceleratorName}: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly CompiledModel _model;
        private readonly IList<TensorBuffer> _inputs;
        private readonly IList<TensorBuffer> _outputs;
        private readonly int _boxesOutputIndex;
        private readonly int _scoresOutputIndex;
        private bool _disposed;

        private Session(
            CompiledModel model,
            IList<TensorBuffer> inputs,
            IList<TensorBuffer> outputs,
            int boxesOutputIndex,
            int scoresOutputIndex,
            string acceleratorName)
        {
            _model = model;
            _inputs = inputs;
            _outputs = outputs;
            _boxesOutputIndex = boxesOutputIndex;
            _scoresOutputIndex = scoresOutputIndex;
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
                        $"Expected one LiteRT input and two outputs, got {inputs.Count} and {outputs.Count}.");
                }

                var warmInput = new float[YoloV9RawPlateDetector.InputValueCount];
                inputs[0].WriteFloat(warmInput);
                model.Run(inputs, outputs);
                var firstOutput = outputs[0].ReadFloat();
                var secondOutput = outputs[1].ReadFloat();
                var (boxesOutputIndex, scoresOutputIndex) = IdentifyOutputs(firstOutput, secondOutput);

                var session = new Session(
                    model,
                    inputs,
                    outputs,
                    boxesOutputIndex,
                    scoresOutputIndex,
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

        public YoloV9RawModelOutput Run(float[] input)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _inputs[0].WriteFloat(input);
            _model.Run(_inputs, _outputs);
            return new YoloV9RawModelOutput(
                _outputs[_boxesOutputIndex].ReadFloat(),
                _outputs[_scoresOutputIndex].ReadFloat());
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

        private static (int Boxes, int Scores) IdentifyOutputs(
            float[] first,
            float[] second)
        {
            var boxesLength = CandidateCount * BoxCoordinateCount;
            if (first.Length == boxesLength && second.Length == CandidateCount)
            {
                return (0, 1);
            }

            if (second.Length == boxesLength && first.Length == CandidateCount)
            {
                return (1, 0);
            }

            throw new InvalidDataException(
                $"Unexpected LiteRT output lengths {first.Length} and {second.Length}; "
                + $"expected {boxesLength} boxes and {CandidateCount} scores.");
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
