using System.Diagnostics;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference.Preprocessing;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Inference.Yolo;

/// <summary>
/// Shared detector pipeline for raw YOLOv9 outputs. A platform-specific model
/// runner supplies fixed box and score tensors; every other step is identical.
/// </summary>
public sealed class YoloV9RawPlateDetector : IPlateDetector, IInferenceBackendInfo, IDisposable
{
    public const int InputValueCount = 3 * DetectorPreprocessor.InputSize * DetectorPreprocessor.InputSize;

    private readonly IYoloV9RawModel _model;
    private readonly float[] _input = new float[InputValueCount];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RecognitionTuningConfiguration _configuration;
    private bool _disposed;

    public YoloV9RawPlateDetector(
        IYoloV9RawModel model,
        RecognitionTuningConfiguration? configuration = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _configuration = configuration ?? new RecognitionTuningConfiguration();
        _configuration.Validate();
    }

    public string BackendName => _model.BackendName;

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
            ObjectDisposedException.ThrowIf(_disposed, this);
            var stageStartedAt = Stopwatch.GetTimestamp();
            var source = _configuration.Detector_RoadRegion.ToPixels(frame.OrientedWidth, frame.OrientedHeight);
            var transform = DetectorPreprocessor.Fill(frame, source, _input, _model.InputLayout);
            var preprocessingMilliseconds = Stopwatch.GetElapsedTime(stageStartedAt).TotalMilliseconds;

            cancellationToken.ThrowIfCancellationRequested();
            stageStartedAt = Stopwatch.GetTimestamp();
            var output = _model.Run(_input);
            var inferenceMilliseconds = Stopwatch.GetElapsedTime(stageStartedAt).TotalMilliseconds;

            stageStartedAt = Stopwatch.GetTimestamp();
            var detections = YoloV9RawPostprocessor.Process(
                output.Boxes,
                output.Scores,
                transform,
                frame.OrientedWidth,
                frame.OrientedHeight,
                _configuration);
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
        _model.Dispose();
        _gate.Dispose();
    }
}
