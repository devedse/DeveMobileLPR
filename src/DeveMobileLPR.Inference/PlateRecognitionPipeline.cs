using System.Diagnostics;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference.Preprocessing;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Inference;

public sealed class PlateRecognitionPipeline : IFrameRecognitionPipeline, IDisposable
{
    private readonly IPlateDetector _detector;
    private readonly IPlateRecognizer _recognizer;
    private readonly RecognitionTuningConfiguration _configuration;
    private readonly string _detectorBackend;
    private readonly string _ocrBackend;

    public PlateRecognitionPipeline(
        IPlateDetector detector,
        IPlateRecognizer recognizer,
        RecognitionTuningConfiguration? configuration = null)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
        _configuration = configuration ?? new RecognitionTuningConfiguration();
        _configuration.Validate();
        _detectorBackend = (detector as IInferenceBackendInfo)?.BackendName ?? detector.GetType().Name;
        _ocrBackend = (recognizer as IInferenceBackendInfo)?.BackendName ?? recognizer.GetType().Name;
    }

    public async ValueTask<FrameRecognition> ProcessAsync(Yuv420Frame frame, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var detectionResult = await _detector.DetectAsync(frame, cancellationToken).ConfigureAwait(false);
        var detections = detectionResult.Detections;
        var observations = new List<PlateObservation>(Math.Min(
            detections.Count,
            _configuration.Detector_MaximumOcrAttemptsPerFrame));
        var candidates = new List<PlateCandidateDiagnostics>(detections.Count);
        var ocrTiming = ModelExecutionTiming.Empty;
        var cropQualityMilliseconds = 0d;
        var ocrAttemptCount = 0;
        foreach (var detection in detections.OrderByDescending(static detection => detection.Confidence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ocrAttemptCount >= _configuration.Detector_MaximumOcrAttemptsPerFrame)
            {
                candidates.Add(new PlateCandidateDiagnostics(detection, null, false, null, null, null));
                continue;
            }

            var qualityStartedAt = Stopwatch.GetTimestamp();
            var quality = CropQualityEstimator.Estimate(frame, detection.Bounds, _configuration);
            cropQualityMilliseconds += Stopwatch.GetElapsedTime(qualityStartedAt).TotalMilliseconds;
            var recognitionResult = await _recognizer.RecognizeAsync(frame, detection.Bounds, cancellationToken).ConfigureAwait(false);
            var read = recognitionResult.Read;
            ocrTiming += recognitionResult.Timing;
            ocrAttemptCount++;
            candidates.Add(new PlateCandidateDiagnostics(
                detection,
                quality,
                true,
                read.Text,
                read.Confidence,
                recognitionResult.Timing));
            if (!string.IsNullOrWhiteSpace(read.Text))
            {
                observations.Add(new PlateObservation(
                    frame.Sequence,
                    frame.CapturedAt,
                    detection,
                    read,
                    quality));
            }
        }

        return new FrameRecognition(frame.Sequence, frame.CapturedAt, observations)
        {
            SourceWidth = frame.OrientedWidth,
            SourceHeight = frame.OrientedHeight,
            RotationDegrees = frame.RotationDegrees,
            Diagnostics = new RecognitionFrameDiagnostics(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                detectionResult.Timing,
                ocrTiming,
                detections.Count,
                ocrAttemptCount,
                observations.Count)
            {
                Candidates = candidates,
                CropQualityMilliseconds = cropQualityMilliseconds,
                DetectorBackend = _detectorBackend,
                OcrBackend = _ocrBackend
            }
        };
    }

    public void Dispose()
    {
        (_detector as IDisposable)?.Dispose();
        if (!ReferenceEquals(_detector, _recognizer))
        {
            (_recognizer as IDisposable)?.Dispose();
        }
    }
}
