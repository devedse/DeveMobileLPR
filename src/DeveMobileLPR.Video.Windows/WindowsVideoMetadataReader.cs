using DeveMobileLPR.Recognition;
using Windows.Media.Editing;
using Windows.Storage;

namespace DeveMobileLPR.Video.Windows;

/// <summary>
/// Reads the source timeline through the same Windows media stack used by the app.
/// </summary>
public static class WindowsVideoMetadataReader
{
    public static async Task<VideoFrameTimeline> ReadTimelineAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();

        var file = await StorageFile.GetFileFromPathAsync(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        var clip = await MediaClip.CreateFromFileAsync(file);
        cancellationToken.ThrowIfCancellationRequested();
        var properties = clip.GetVideoEncodingProperties();
        return CreateTimeline(
            clip.OriginalDuration,
            properties.FrameRate.Numerator,
            properties.FrameRate.Denominator);
    }

    public static VideoFrameTimeline CreateTimeline(
        TimeSpan duration,
        uint frameRateNumerator,
        uint frameRateDenominator)
    {
        var frameRate = frameRateDenominator == 0
            ? null
            : (double?)frameRateNumerator / frameRateDenominator;
        return VideoFrameTimeline.Create(duration, frameRate, null);
    }
}
