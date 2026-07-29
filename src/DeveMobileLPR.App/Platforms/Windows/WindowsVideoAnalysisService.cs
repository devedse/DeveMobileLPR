using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Inference.Models;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Video.Windows;
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
        VideoAnalysisOptions options,
        IProgress<VideoAnalysisProgress>? progress,
        Action<string>? diagnostic,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var engine = await EnsureEngineAsync(diagnostic, cancellationToken).ConfigureAwait(false);
        var (_, timeline) = await OpenCompositionAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        using var source = WindowsMediaFoundationVideoFrameSource.Create(sourcePath, timeline);
        return await engine.AnalyzeAsync(source, sourcePath, displayName, options, progress, cancellationToken).ConfigureAwait(false);
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

            var modelDirectory = Path.Combine(AppContext.BaseDirectory, "models");
            var detectorPath = Path.Combine(modelDirectory, ModelCatalog.Detector.FileName);
            var recognizerPath = Path.Combine(modelDirectory, ModelCatalog.Recognizer.FileName);
            await ModelArtifactVerifier.VerifyAsync(detectorPath, ModelCatalog.Detector, cancellationToken).ConfigureAwait(false);
            await ModelArtifactVerifier.VerifyAsync(recognizerPath, ModelCatalog.Recognizer, cancellationToken).ConfigureAwait(false);
            _engine = new VideoAnalysisEngine(OnnxPlateRecognitionPipelineFactory.Create(detectorPath, recognizerPath, diagnostic));
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
        var timeline = WindowsVideoMetadataReader.CreateTimeline(
            clip.OriginalDuration,
            properties.FrameRate.Numerator,
            properties.FrameRate.Denominator);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        return (composition, timeline);
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
