using System.Buffers;
using Android.Graphics;
using Android.Media;
using DeveMobileLPR.App.Infrastructure;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Recognition;
using IOPath = System.IO.Path;

namespace DeveMobileLPR.App.Services;

internal sealed class VideoAnalysisService : IDisposable
{
    private const int PreviewWidth = 1280;
    private static readonly string StagingDirectory = IOPath.Combine(FileSystem.AppDataDirectory, "video-sources");
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private VideoAnalysisEngine? _engine;
    private bool _disposed;

    public VideoAnalysisService()
    {
    }

    public async Task<string> StageAsync(FileResult file, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Directory.CreateDirectory(StagingDirectory);
        var extension = IOPath.GetExtension(file.FileName);
        var target = IOPath.Combine(StagingDirectory, $"{Guid.NewGuid():N}{extension}");
        await using var source = await file.OpenReadAsync().ConfigureAwait(false);
        await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024, true);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return target;
    }

    public async Task<VideoAnalysisResult> AnalyzeAsync(
        string sourcePath,
        string displayName,
        VideoAnalysisOptions options,
        IProgress<VideoAnalysisProgress>? progress,
        Action<string>? diagnostic,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var engine = await EnsureEngineAsync(diagnostic, cancellationToken).ConfigureAwait(false);
        using var source = new AndroidVideoFrameSource(sourcePath);
        return await engine.AnalyzeAsync(source, sourcePath, displayName, options, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task<byte[]> GetPreviewAsync(string sourcePath, TimeSpan position, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var retriever = CreateRetriever(sourcePath);
            var metadata = ReadMetadata(retriever);
            var previewWidth = Math.Min(PreviewWidth, metadata.FrameWidth);
            var previewHeight = Math.Max(1, checked((int)Math.Round(metadata.FrameHeight * previewWidth / (double)metadata.FrameWidth)));
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

    private async Task<VideoAnalysisEngine> EnsureEngineAsync(
        Action<string>? diagnostic,
        CancellationToken cancellationToken)
    {
        if (_engine is not null)
        {
            return _engine;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_engine is not null)
            {
                return _engine;
            }

            var context = global::Android.App.Application.Context;
            var files = context.FilesDir?.AbsolutePath ?? FileSystem.AppDataDirectory;
            var models = await AndroidModelInstaller.EnsureInstalledAsync(
                context.Assets ?? throw new InvalidOperationException("Application assets are unavailable."),
                files,
                cancellationToken).ConfigureAwait(false);
            _engine = new VideoAnalysisEngine(OnnxPlateRecognitionPipelineFactory.Create(models.Detector, models.Ocr, diagnostic));
            return _engine;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

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

    private static Bitmap? GetAnalysisFrame(MediaMetadataRetriever retriever, long timeMicroseconds, int width, int height) =>
        OperatingSystem.IsAndroidVersionAtLeast(27)
            ? retriever.GetScaledFrameAtTime(timeMicroseconds, Option.Closest, width, height)
            : retriever.GetFrameAtTime(timeMicroseconds, Option.Closest);

    private static VideoMetadata ReadMetadata(MediaMetadataRetriever retriever)
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
        return new VideoMetadata(
            timeline,
            checked((int)sourceWidth),
            checked((int)sourceHeight));
    }

    private static double ParsePositiveDouble(string? value, string name) =>
        ParseOptionalPositiveDouble(value)
        ?? throw new InvalidDataException($"The selected video does not report a valid {name}.");

    private static double? ParseOptionalPositiveDouble(string? value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
        && parsed > 0
            ? parsed
            : null;

    private static Yuv420Frame BitmapToYuv420Frame(Bitmap bitmap, long sequence, TimeSpan position)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var pixelCount = checked(width * height);
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        var chromaLength = checked(chromaWidth * chromaHeight);
        var pixels = ArrayPool<int>.Shared.Rent(pixelCount);
        var yOwner = MemoryPool<byte>.Shared.Rent(pixelCount);
        var uOwner = MemoryPool<byte>.Shared.Rent(chromaLength);
        var vOwner = MemoryPool<byte>.Shared.Rent(chromaLength);
        try
        {
            bitmap.GetPixels(pixels, 0, width, 0, 0, width, height);
            var yPlane = yOwner.Memory.Span[..pixelCount];
            var uPlane = uOwner.Memory.Span[..chromaLength];
            var vPlane = vOwner.Memory.Span[..chromaLength];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var color = pixels[y * width + x];
                    var red = (color >> 16) & 0xff;
                    var green = (color >> 8) & 0xff;
                    var blue = color & 0xff;
                    yPlane[y * width + x] = Clamp((66 * red + 129 * green + 25 * blue + 128 >> 8) + 16);
                }
            }

            for (var y = 0; y < height; y += 2)
            {
                for (var x = 0; x < width; x += 2)
                {
                    var color = pixels[y * width + x];
                    var red = (color >> 16) & 0xff;
                    var green = (color >> 8) & 0xff;
                    var blue = color & 0xff;
                    var chromaIndex = y / 2 * chromaWidth + x / 2;
                    uPlane[chromaIndex] = Clamp((-38 * red - 74 * green + 112 * blue + 128 >> 8) + 128);
                    vPlane[chromaIndex] = Clamp((112 * red - 94 * green - 18 * blue + 128 >> 8) + 128);
                }
            }

            var frame = new Yuv420Frame(
                sequence,
                DateTimeOffset.UnixEpoch + position,
                width,
                height,
                0,
                yOwner,
                pixelCount,
                width,
                1,
                uOwner,
                chromaLength,
                chromaWidth,
                1,
                vOwner,
                chromaLength,
                chromaWidth,
                1);
            yOwner = null!;
            uOwner = null!;
            vOwner = null!;
            return frame;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(pixels);
            yOwner?.Dispose();
            uOwner?.Dispose();
            vOwner?.Dispose();
        }
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private sealed record VideoMetadata(VideoFrameTimeline Timeline, int FrameWidth, int FrameHeight);

    private sealed class AndroidVideoFrameSource : IVideoFrameSource
    {
        private readonly MediaMetadataRetriever _retriever;
        private readonly VideoMetadata _metadata;

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

        public void Dispose() => _retriever.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine?.Dispose();
        _initializationGate.Dispose();
    }
}
