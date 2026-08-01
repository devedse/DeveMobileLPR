using DeveMobileLPR.Inference;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application;

public sealed class VideoAnalysisService : IDisposable
{
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly RecognitionTuningConfiguration _recognitionTuning;
    private readonly IRecognitionPipelineProvider _pipelineProvider;
    private readonly IVideoFileBackend _backend;
    private VideoAnalysisEngine? _engine;
    private bool _disposed;

    public VideoAnalysisService(
        RecognitionTuningConfiguration recognitionTuning,
        IRecognitionPipelineProvider pipelineProvider,
        IVideoFileBackend backend)
    {
        _recognitionTuning = recognitionTuning;
        _pipelineProvider = pipelineProvider;
        _backend = backend;
    }

    public Task<string> StageAsync(SelectedVideoFile file, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.StageAsync(file, cancellationToken);
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
        using var source = await _backend.OpenFrameSourceAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        return await engine.AnalyzeAsync(
            source,
            sourcePath,
            displayName,
            options,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<byte[]> GetPreviewAsync(
        string sourcePath,
        TimeSpan position,
        CancellationToken cancellationToken) =>
        _backend.GetPreviewAsync(sourcePath, position, cancellationToken);

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
            if (_engine is null)
            {
                _engine = new VideoAnalysisEngine(
                    await _pipelineProvider.CreateAsync(diagnostic, cancellationToken).ConfigureAwait(false),
                    _recognitionTuning);
            }

            return _engine;
        }
        finally
        {
            _initializationGate.Release();
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
