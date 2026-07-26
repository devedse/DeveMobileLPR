using System.Diagnostics;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference.Preprocessing;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Inference;

public sealed class PlateRecognitionPipeline(
    IPlateDetector detector,
    IPlateRecognizer recognizer,
    int maximumPlatesPerFrame = 6) : IFrameRecognitionPipeline, IDisposable
{
    public async ValueTask<FrameRecognition> ProcessAsync(Yuv420Frame frame, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var detections = await detector.DetectAsync(frame, cancellationToken).ConfigureAwait(false);
        var observations = new List<PlateObservation>(Math.Min(detections.Count, maximumPlatesPerFrame));
        foreach (var detection in detections
                     .OrderByDescending(static detection => detection.Confidence)
                     .Take(maximumPlatesPerFrame))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quality = CropQualityEstimator.Estimate(frame, detection.Bounds);
            var read = await recognizer.RecognizeAsync(frame, detection.Bounds, cancellationToken).ConfigureAwait(false);
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

        return new FrameRecognition(frame.Sequence, frame.CapturedAt, stopwatch.Elapsed, observations);
    }

    public void Dispose()
    {
        (detector as IDisposable)?.Dispose();
        if (!ReferenceEquals(detector, recognizer))
        {
            (recognizer as IDisposable)?.Dispose();
        }
    }
}
