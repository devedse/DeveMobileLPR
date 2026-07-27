using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Recognition;

public interface IVideoFrameSource : IDisposable
{
    VideoFrameTimeline Timeline { get; }

    ValueTask<Yuv420Frame?> DecodeAsync(
        long sourceFrameIndex,
        TimeSpan position,
        CancellationToken cancellationToken);
}

public sealed record VideoAnalysisProgress(
    int ProcessedFrames,
    int TotalFrames,
    TimeSpan Position)
{
    public double Fraction => TotalFrames == 0 ? 0 : (double)ProcessedFrames / TotalFrames;
}

public sealed record AnalyzedVideoFrame(
    long SourceFrameIndex,
    TimeSpan Position,
    IReadOnlyList<AnalyzedPlateRead> Reads,
    IReadOnlyList<AnalyzedPlateConfirmation> Confirmations)
{
    public bool HasDetections => Reads.Count > 0 || Confirmations.Count > 0;
}

public sealed record AnalyzedPlateRead(
    string Text,
    float OcrConfidence,
    float DetectorConfidence);

public sealed record AnalyzedPlateConfirmation(
    string NormalizedPlate,
    string DisplayPlate,
    float Confidence,
    int ObservationCount);

public sealed record VideoAnalysisResult(
    Guid Id,
    string SourcePath,
    string DisplayName,
    DateTimeOffset AnalyzedAt,
    TimeSpan Duration,
    double SourceFrameRate,
    int SourceFrameCount,
    VideoFrameSampling Sampling,
    IReadOnlyList<AnalyzedVideoFrame> Frames);

public static class VideoFrameNavigation
{
    public static int FindClosestFrameIndex(IReadOnlyList<AnalyzedVideoFrame> frames, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            throw new ArgumentException("At least one analyzed frame is required.", nameof(frames));
        }

        var low = 0;
        var high = frames.Count - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (frames[middle].Position < position) low = middle + 1;
            else high = middle;
        }
        return low > 0 && position - frames[low - 1].Position <= frames[low].Position - position ? low - 1 : low;
    }
}