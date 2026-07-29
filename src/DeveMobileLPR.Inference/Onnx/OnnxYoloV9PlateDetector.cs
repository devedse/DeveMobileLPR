using System.Diagnostics;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference.Preprocessing;
using DeveMobileLPR.Recognition;
using Microsoft.ML.OnnxRuntime;

namespace DeveMobileLPR.Inference.Onnx;

public sealed class OnnxYoloV9PlateDetector : IPlateDetector, IDisposable
{
    private static readonly long[] InputShape = [1, 3, DetectorPreprocessor.InputSize, DetectorPreprocessor.InputSize];
    private readonly InferenceSession _session;
    private readonly float[] _input = new float[3 * DetectorPreprocessor.InputSize * DetectorPreprocessor.InputSize];
    private readonly OrtValue _inputValue;
    private readonly Dictionary<string, OrtValue> _inputs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly float _confidenceThreshold;
    private readonly NormalizedRegion _roadRegion;
    private bool _disposed;

    public OnnxYoloV9PlateDetector(
        string modelPath,
        float confidenceThreshold = 0.32f,
        NormalizedRegion? roadRegion = null,
        int xnnpackThreads = 4,
        Action<string>? diagnostic = null)
    {
        _confidenceThreshold = confidenceThreshold;
        _roadRegion = roadRegion ?? NormalizedRegion.RoadDefault;
        _session = OnnxSessionFactory.Create(modelPath, xnnpackThreads, diagnostic);
        ValidateInputContract();
        _inputValue = OrtValue.CreateTensorValueFromMemory(_input, InputShape);
        _inputs = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
        {
            [_session.InputNames[0]] = _inputValue
        };
    }

    public async ValueTask<PlateDetectionResult> DetectAsync(
        Yuv420Frame frame,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var queuedAt = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var queueMilliseconds = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
        try
        {
            var stageStartedAt = Stopwatch.GetTimestamp();
            var source = _roadRegion.ToPixels(frame.OrientedWidth, frame.OrientedHeight);
            var transform = DetectorPreprocessor.Fill(frame, source, _input);
            var preprocessingMilliseconds = Stopwatch.GetElapsedTime(stageStartedAt).TotalMilliseconds;

            stageStartedAt = Stopwatch.GetTimestamp();
            using var runOptions = new RunOptions();
            using var output = _session.Run(runOptions, _inputs, _session.OutputNames);
            var inferenceMilliseconds = Stopwatch.GetElapsedTime(stageStartedAt).TotalMilliseconds;

            stageStartedAt = Stopwatch.GetTimestamp();
            var tensor = output[0];
            var values = tensor.GetTensorDataAsSpan<float>();
            var dimensions = tensor.GetTensorTypeAndShape().Shape;
            var rowWidth = dimensions.Length > 0 ? checked((int)dimensions[^1]) : 7;
            if (rowWidth < 7 || values.Length % rowWidth != 0)
            {
                throw new InvalidDataException($"Unexpected detector output shape [{string.Join(',', dimensions)}].");
            }

            var detections = new List<PlateDetection>();
            for (var offset = 0; offset + 6 < values.Length; offset += rowWidth)
            {
                var score = values[offset + 6];
                if (score < _confidenceThreshold)
                {
                    continue;
                }

                var modelBounds = new BoundingBox(
                    values[offset + 1],
                    values[offset + 2],
                    values[offset + 3],
                    values[offset + 4]);
                var sourceBounds = transform.ToSource(modelBounds, frame.OrientedWidth, frame.OrientedHeight);
                if (!sourceBounds.IsEmpty && sourceBounds.Width >= 12 && sourceBounds.Height >= 5)
                {
                    detections.Add(new PlateDetection(sourceBounds, score));
                }
            }

            var postprocessingMilliseconds = Stopwatch.GetElapsedTime(stageStartedAt).TotalMilliseconds;
            return new PlateDetectionResult(
                detections,
                new ModelExecutionTiming(
                    queueMilliseconds,
                    preprocessingMilliseconds,
                    inferenceMilliseconds,
                    postprocessingMilliseconds));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inputValue.Dispose();
        _session.Dispose();
        _gate.Dispose();
    }

    private void ValidateInputContract()
    {
        var input = _session.InputMetadata.Single().Value;
        var dimensions = input.Dimensions;
        if (dimensions.Length != 4 || dimensions[1] != 3 || dimensions[2] != DetectorPreprocessor.InputSize || dimensions[3] != DetectorPreprocessor.InputSize)
        {
            throw new InvalidDataException($"Expected detector input [1,3,608,608], got [{string.Join(',', dimensions)}].");
        }
    }
}
