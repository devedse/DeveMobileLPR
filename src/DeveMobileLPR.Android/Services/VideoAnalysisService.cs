using System.Buffers;
using Android.Graphics;
using Android.Media;
using DeveMobileLPR.AndroidApp.Infrastructure;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Recognition;
using IOPath = System.IO.Path;

namespace DeveMobileLPR.AndroidApp.Services;

internal sealed class VideoAnalysisService : IDisposable
{
    private const int DecodeWidth = 1280;
    private static readonly string StagingDirectory = IOPath.Combine(FileSystem.AppDataDirectory, "video-sources");
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private PlateRecognitionPipeline? _pipeline;
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
        VideoFrameSampling sampling,
        IProgress<VideoAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pipeline = await EnsurePipelineAsync(cancellationToken).ConfigureAwait(false);
            using var retriever = CreateRetriever(sourcePath);
            var metadata = ReadMetadata(retriever);
            var sampledFrameCount = (metadata.Timeline.FrameCount + sampling.Interval - 1) / sampling.Interval;
            var frames = new List<AnalyzedVideoFrame>(sampledFrameCount);
            var tracks = new PlateTrackManager();
            var processedFrames = 0;

            for (var sourceFrameIndex = 0; sourceFrameIndex < metadata.Timeline.FrameCount; sourceFrameIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!sampling.Includes(sourceFrameIndex))
                {
                    continue;
                }

                var position = metadata.Timeline.PositionOf(sourceFrameIndex);
                using var bitmap = GetAnalysisFrame(
                    retriever,
                    checked((long)(position.TotalMilliseconds * 1000)),
                    metadata.DecodeWidth,
                    metadata.DecodeHeight);
                if (bitmap is not null)
                {
                    using var frame = BitmapToYuv420Frame(bitmap, sourceFrameIndex + 1, position);
                    var recognition = await pipeline.ProcessAsync(frame, cancellationToken).ConfigureAwait(false);
                    frames.Add(CreateAnalyzedFrame(sourceFrameIndex, position, recognition, tracks.Update(recognition)));
                }

                processedFrames++;
                progress?.Report(new VideoAnalysisProgress(processedFrames, sampledFrameCount, position));
            }

            return new VideoAnalysisResult(
                Guid.NewGuid(),
                sourcePath,
                displayName,
                DateTimeOffset.UtcNow,
                metadata.Timeline.Duration,
                metadata.Timeline.FrameRate,
                metadata.Timeline.FrameCount,
                sampling,
                frames);
        }
        finally
        {
            _runGate.Release();
        }
    }

    public Task<byte[]> GetPreviewAsync(string sourcePath, TimeSpan position, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var retriever = CreateRetriever(sourcePath);
            var metadata = ReadMetadata(retriever);
            using var bitmap = GetAnalysisFrame(
                retriever,
                checked((long)(position.TotalMilliseconds * 1000)),
                metadata.DecodeWidth,
                metadata.DecodeHeight)
                ?? throw new InvalidDataException("The selected video frame could not be decoded.");
            using var stream = new MemoryStream();
            if (!bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 88, stream))
            {
                throw new InvalidDataException("The selected video frame could not be rendered.");
            }
            return stream.ToArray();
        }, cancellationToken);

    private async Task<PlateRecognitionPipeline> EnsurePipelineAsync(CancellationToken cancellationToken)
    {
        if (_pipeline is not null)
        {
            return _pipeline;
        }

        var context = global::Android.App.Application.Context;
        var files = context.FilesDir?.AbsolutePath ?? FileSystem.AppDataDirectory;
        var models = await AndroidModelInstaller.EnsureInstalledAsync(
            context.Assets ?? throw new InvalidOperationException("Application assets are unavailable."),
            files,
            cancellationToken).ConfigureAwait(false);
        _pipeline = OnnxPlateRecognitionPipelineFactory.Create(models.Detector, models.Ocr);
        return _pipeline;
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
        var decodeWidth = checked((int)Math.Min(DecodeWidth, sourceWidth));
        var decodeHeight = Math.Max(1, checked((int)Math.Round(sourceHeight * decodeWidth / sourceWidth)));
        return new VideoMetadata(timeline, decodeWidth, decodeHeight);
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

    private static AnalyzedVideoFrame CreateAnalyzedFrame(
        long sourceFrameIndex,
        TimeSpan position,
        FrameRecognition recognition,
        IReadOnlyList<ConfirmedPlate> confirmations) => new(
            sourceFrameIndex,
            position,
            recognition.Observations.Select(static observation => new AnalyzedPlateRead(
                observation.Read.Text,
                observation.Read.Confidence,
                observation.Detection.Confidence)).ToArray(),
            confirmations.Select(static confirmation => new AnalyzedPlateConfirmation(
                confirmation.Consensus.NormalizedPlate,
                confirmation.Consensus.DisplayPlate,
                confirmation.Consensus.Confidence,
                confirmation.Consensus.ObservationCount)).ToArray());

    private sealed record VideoMetadata(VideoFrameTimeline Timeline, int DecodeWidth, int DecodeHeight);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pipeline?.Dispose();
        _runGate.Dispose();
    }
}