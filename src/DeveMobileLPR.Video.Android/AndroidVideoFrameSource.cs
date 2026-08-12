using System.Buffers;
using Android.Graphics;
using Android.Media;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Video.Android;

public sealed class AndroidVideoFrameSource : IVideoFrameSource
{
    private const int PreviewWidth = 1280;
    private readonly MediaMetadataRetriever _retriever;
    private readonly AndroidVideoMetadata _metadata;

    public AndroidVideoFrameSource(string sourcePath)
    {
        _retriever = CreateRetriever(sourcePath);
        try
        {
            _metadata = ReadMetadata(_retriever);
        }
        catch
        {
            _retriever.Dispose();
            throw;
        }
    }

    public VideoFrameTimeline Timeline => _metadata.Timeline;

    public ValueTask<Yuv420Frame?> DecodeAsync(
        long sourceFrameIndex,
        TimeSpan position,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = GetAnalysisFrame(
            _retriever,
            checked((long)(position.TotalMilliseconds * 1000)),
            _metadata.FrameWidth,
            _metadata.FrameHeight);
        return ValueTask.FromResult<Yuv420Frame?>(
            bitmap is null ? null : BitmapToYuv420Frame(bitmap, sourceFrameIndex + 1, position));
    }

    public static Task<byte[]> GetPreviewAsync(
        string sourcePath,
        TimeSpan position,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var retriever = CreateRetriever(sourcePath);
        var metadata = ReadMetadata(retriever);
        var previewWidth = Math.Min(PreviewWidth, metadata.FrameWidth);
        var previewHeight = Math.Max(1, checked((int)Math.Round(
            metadata.FrameHeight * previewWidth / (double)metadata.FrameWidth)));
        using var bitmap = GetAnalysisFrame(
            retriever,
            checked((long)(position.TotalMilliseconds * 1000)),
            previewWidth,
            previewHeight)
            ?? throw new InvalidDataException("The selected video frame could not be decoded.");
        using var stream = new MemoryStream();
        if (!bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 88, stream))
        {
            throw new InvalidDataException("The selected video frame could not be rendered.");
        }

        return stream.ToArray();
    }, cancellationToken);

    private static MediaMetadataRetriever CreateRetriever(string sourcePath)
    {
        var retriever = new MediaMetadataRetriever();
        try
        {
            retriever.SetDataSource(sourcePath);
            return retriever;
        }
        catch
        {
            retriever.Dispose();
            throw;
        }
    }

    private static Bitmap? GetAnalysisFrame(
        MediaMetadataRetriever retriever,
        long timeMicroseconds,
        int width,
        int height) => OperatingSystem.IsAndroidVersionAtLeast(27)
        ? retriever.GetScaledFrameAtTime(timeMicroseconds, Option.Closest, width, height)
        : retriever.GetFrameAtTime(timeMicroseconds, Option.Closest);

    private static AndroidVideoMetadata ReadMetadata(MediaMetadataRetriever retriever)
    {
        var durationMilliseconds = ParsePositiveDouble(retriever.ExtractMetadata(MetadataKey.Duration), "duration");
        var reportedFrameRate = ParseOptionalPositiveDouble(retriever.ExtractMetadata(MetadataKey.CaptureFramerate));
        var reportedFrameCount = OperatingSystem.IsAndroidVersionAtLeast(28)
            ? ParseOptionalPositiveDouble(retriever.ExtractMetadata(MetadataKey.VideoFrameCount))
            : null;
        var timeline = VideoFrameTimeline.Create(
            TimeSpan.FromMilliseconds(durationMilliseconds),
            reportedFrameRate,
            reportedFrameCount is null ? null : checked((int)Math.Ceiling(reportedFrameCount.Value)));
        var sourceWidth = ParsePositiveDouble(retriever.ExtractMetadata(MetadataKey.VideoWidth), "video width");
        var sourceHeight = ParsePositiveDouble(retriever.ExtractMetadata(MetadataKey.VideoHeight), "video height");
        return new AndroidVideoMetadata(
            timeline,
            checked((int)sourceWidth),
            checked((int)sourceHeight));
    }

    private static double ParsePositiveDouble(string? value, string name) =>
        ParseOptionalPositiveDouble(value)
        ?? throw new InvalidDataException($"The selected video does not report a valid {name}.");

    private static double? ParseOptionalPositiveDouble(string? value) =>
        double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
        && parsed > 0
            ? parsed
            : null;

    private static Yuv420Frame BitmapToYuv420Frame(Bitmap bitmap, long sequence, TimeSpan position)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var pixelCount = checked(width * height);
        var pixels = ArrayPool<int>.Shared.Rent(pixelCount);
        try
        {
            bitmap.GetPixels(pixels, 0, width, 0, 0, width, height);
            return ArgbFrameFactory.Create(
                pixels.AsSpan(0, pixelCount),
                width,
                height,
                sequence,
                DateTimeOffset.UnixEpoch + position);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(pixels);
        }
    }

    public void Dispose() => _retriever.Dispose();
}

internal sealed record AndroidVideoMetadata(
    VideoFrameTimeline Timeline,
    int FrameWidth,
    int FrameHeight);
