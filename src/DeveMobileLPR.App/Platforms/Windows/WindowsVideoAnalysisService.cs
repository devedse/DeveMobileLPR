using System.Buffers;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Inference.Models;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Recognition;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeveMobileLPR.App.Services;

internal sealed class VideoAnalysisService : IDisposable
{
    private const int DecodeWidth = 1280;
    private static readonly string StagingDirectory = Path.Combine(FileSystem.CacheDirectory, "video-analysis");
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private VideoAnalysisEngine? _engine;
    private bool _disposed;

    public VideoAnalysisService()
    {
    }

    public async Task<string> StageAsync(FileResult file, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.IsNullOrWhiteSpace(file.FullPath) && File.Exists(file.FullPath))
        {
            return file.FullPath;
        }

        Directory.CreateDirectory(StagingDirectory);
        var target = Path.Combine(StagingDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
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
        var engine = await EnsureEngineAsync(cancellationToken).ConfigureAwait(false);
        using var source = await WindowsVideoFrameSource.CreateAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        return await engine.AnalyzeAsync(source, sourcePath, displayName, sampling, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> GetPreviewAsync(string sourcePath, TimeSpan position, CancellationToken cancellationToken)
    {
        var (composition, _) = await OpenCompositionAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        using var thumbnail = await composition.GetThumbnailAsync(position, DecodeWidth, 0, VideoFramePrecision.NearestFrame);
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new DataReader(thumbnail.GetInputStreamAt(0));
        await reader.LoadAsync(checked((uint)thumbnail.Size));
        var bytes = new byte[checked((int)thumbnail.Size)];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task<VideoAnalysisEngine> EnsureEngineAsync(CancellationToken cancellationToken)
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

            var modelDirectory = Path.Combine(AppContext.BaseDirectory, "models");
            var detectorPath = Path.Combine(modelDirectory, ModelCatalog.Detector.FileName);
            var recognizerPath = Path.Combine(modelDirectory, ModelCatalog.Recognizer.FileName);
            await ModelArtifactVerifier.VerifyAsync(detectorPath, ModelCatalog.Detector, cancellationToken).ConfigureAwait(false);
            await ModelArtifactVerifier.VerifyAsync(recognizerPath, ModelCatalog.Recognizer, cancellationToken).ConfigureAwait(false);
            _engine = new VideoAnalysisEngine(OnnxPlateRecognitionPipelineFactory.Create(detectorPath, recognizerPath));
            return _engine;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private static async Task<(MediaComposition Composition, VideoFrameTimeline Timeline)> OpenCompositionAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(sourcePath);
        var clip = await MediaClip.CreateFromFileAsync(file);
        var properties = clip.GetVideoEncodingProperties();
        var denominator = properties.FrameRate.Denominator;
        var frameRate = denominator == 0 ? null : (double?)properties.FrameRate.Numerator / denominator;
        var timeline = VideoFrameTimeline.Create(clip.OriginalDuration, frameRate, null);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        return (composition, timeline);
    }

    private static async Task<Yuv420Frame> DecodeFrameAsync(
        MediaComposition composition,
        TimeSpan position,
        long sequence,
        CancellationToken cancellationToken)
    {
        using var thumbnail = await composition.GetThumbnailAsync(position, DecodeWidth, 0, VideoFramePrecision.NearestFrame);
        cancellationToken.ThrowIfCancellationRequested();
        var decoder = await BitmapDecoder.CreateAsync(thumbnail);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        return BitmapToYuv420Frame(bitmap, sequence, position);
    }

    private static Yuv420Frame BitmapToYuv420Frame(SoftwareBitmap bitmap, long sequence, TimeSpan position)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var pixelCount = checked(width * height);
        var bgraLength = checked(pixelCount * 4);
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        var chromaLength = checked(chromaWidth * chromaHeight);
        var buffer = new Windows.Storage.Streams.Buffer(checked((uint)bgraLength));
        bitmap.CopyToBuffer(buffer);
        var pixels = new byte[bgraLength];
        var yOwner = MemoryPool<byte>.Shared.Rent(pixelCount);
        var uOwner = MemoryPool<byte>.Shared.Rent(chromaLength);
        var vOwner = MemoryPool<byte>.Shared.Rent(chromaLength);
        try
        {
            using var reader = DataReader.FromBuffer(buffer);
            reader.ReadBytes(pixels);
            var yPlane = yOwner.Memory.Span[..pixelCount];
            var uPlane = uOwner.Memory.Span[..chromaLength];
            var vPlane = vOwner.Memory.Span[..chromaLength];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = (y * width + x) * 4;
                    var blue = pixels[offset];
                    var green = pixels[offset + 1];
                    var red = pixels[offset + 2];
                    yPlane[y * width + x] = Clamp((66 * red + 129 * green + 25 * blue + 128 >> 8) + 16);
                }
            }

            for (var y = 0; y < height; y += 2)
            {
                for (var x = 0; x < width; x += 2)
                {
                    var offset = (y * width + x) * 4;
                    var blue = pixels[offset];
                    var green = pixels[offset + 1];
                    var red = pixels[offset + 2];
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
            yOwner?.Dispose();
            uOwner?.Dispose();
            vOwner?.Dispose();
        }
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private sealed class WindowsVideoFrameSource(
        MediaComposition composition,
        VideoFrameTimeline timeline) : IVideoFrameSource
    {
        public VideoFrameTimeline Timeline { get; } = timeline;

        public static async Task<WindowsVideoFrameSource> CreateAsync(
            string sourcePath,
            CancellationToken cancellationToken)
        {
            var (composition, timeline) = await OpenCompositionAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            return new WindowsVideoFrameSource(composition, timeline);
        }

        public async ValueTask<Yuv420Frame?> DecodeAsync(
            long sourceFrameIndex,
            TimeSpan position,
            CancellationToken cancellationToken) =>
            await DecodeFrameAsync(composition, position, sourceFrameIndex + 1, cancellationToken).ConfigureAwait(false);

        public void Dispose()
        {
        }
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