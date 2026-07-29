using System.Diagnostics;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Inference;

public sealed class VideoAnalysisEngine : IDisposable
{
    private readonly RecognitionStreamProcessor _processor;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private bool _disposed;

    public VideoAnalysisEngine(IFrameRecognitionPipeline pipeline)
        : this(new RecognitionStreamProcessor(pipeline))
    {
    }

    public VideoAnalysisEngine(RecognitionStreamProcessor processor)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    public Task<VideoAnalysisResult> AnalyzeAsync(
        IVideoFrameSource source,
        string sourcePath,
        string displayName,
        VideoFrameSampling sampling,
        IProgress<VideoAnalysisProgress>? progress,
        CancellationToken cancellationToken) => AnalyzeAsync(
            source,
            sourcePath,
            displayName,
            new VideoAnalysisOptions(sampling),
            progress,
            cancellationToken);

    public async Task<VideoAnalysisResult> AnalyzeAsync(
        IVideoFrameSource source,
        string sourcePath,
        string displayName,
        VideoAnalysisOptions options,
        IProgress<VideoAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Sampling.Interval < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The video frame interval must be at least one.");
        }
        if (options.MaximumDuration is { } maximumDuration && maximumDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum analysis duration must be positive.");
        }
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var timeline = source.Timeline;
            var duration = options.EffectiveDuration(timeline);
            var sourceFrameCount = Math.Min(
                timeline.FrameCount,
                Math.Max(1, checked((int)Math.Ceiling(duration.TotalSeconds * timeline.FrameRate))));
            var sampling = options.Sampling;
            var sampledFrameCount = (sourceFrameCount + sampling.Interval - 1) / sampling.Interval;
            var frames = new List<AnalyzedVideoFrame>(sampledFrameCount);
            _processor.Reset();
            var processedFrames = 0;
            var stopwatch = Stopwatch.StartNew();
            var decodeElapsed = TimeSpan.Zero;
            var recognitionElapsed = TimeSpan.Zero;

            for (var sourceFrameIndex = 0; sourceFrameIndex < sourceFrameCount; sourceFrameIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!sampling.Includes(sourceFrameIndex))
                {
                    continue;
                }

                var position = timeline.PositionOf(sourceFrameIndex);
                var stageStartedAt = stopwatch.Elapsed;
                using var frame = await source.DecodeAsync(sourceFrameIndex, position, cancellationToken).ConfigureAwait(false);
                decodeElapsed += stopwatch.Elapsed - stageStartedAt;
                if (frame is not null)
                {
                    stageStartedAt = stopwatch.Elapsed;
                    var result = await _processor.ProcessAsync(frame, cancellationToken).ConfigureAwait(false);
                    frames.Add(CreateAnalyzedFrame(sourceFrameIndex, position, result, options.IncludeDiagnostics));
                    recognitionElapsed += stopwatch.Elapsed - stageStartedAt;
                }

                processedFrames++;
                progress?.Report(new VideoAnalysisProgress(
                    processedFrames,
                    sampledFrameCount,
                    position,
                    stopwatch.Elapsed,
                    decodeElapsed,
                    recognitionElapsed));
            }

            return new VideoAnalysisResult(
                Guid.NewGuid(),
                sourcePath,
                displayName,
                DateTimeOffset.UtcNow,
                duration,
                timeline.FrameRate,
                sourceFrameCount,
                sampling,
                frames);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private static AnalyzedVideoFrame CreateAnalyzedFrame(
        long sourceFrameIndex,
        TimeSpan position,
        RecognitionStreamResult result,
        bool includeDiagnostics)
    {
        var recognition = result.Recognition;
        return new AnalyzedVideoFrame(
            sourceFrameIndex,
            position,
            recognition.Observations.Select(static observation => new AnalyzedPlateRead(
                observation.Read.Text,
                observation.Read.Confidence,
                observation.Detection.Confidence,
                observation.Detection.Bounds)).ToArray(),
            result.Confirmations.Select(static confirmation => new AnalyzedPlateConfirmation(
                confirmation.Consensus.NormalizedPlate,
                confirmation.Consensus.DisplayPlate,
                confirmation.Consensus.Confidence,
                confirmation.Consensus.ObservationCount,
                confirmation.LastBounds)).ToArray(),
            recognition.SourceWidth,
            recognition.SourceHeight)
        {
            Diagnostics = includeDiagnostics ? result.Diagnostics : null
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _processor.Dispose();
        _runGate.Dispose();
    }
}
