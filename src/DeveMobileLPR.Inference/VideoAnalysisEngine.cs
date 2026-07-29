using System.Diagnostics;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Inference;

public sealed class VideoAnalysisEngine(IFrameRecognitionPipeline pipeline) : IDisposable
{
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private bool _disposed;

    public async Task<VideoAnalysisResult> AnalyzeAsync(
        IVideoFrameSource source,
        string sourcePath,
        string displayName,
        VideoFrameSampling sampling,
        IProgress<VideoAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var timeline = source.Timeline;
            var sampledFrameCount = (timeline.FrameCount + sampling.Interval - 1) / sampling.Interval;
            var frames = new List<AnalyzedVideoFrame>(sampledFrameCount);
            var tracks = new PlateTrackManager();
            var processedFrames = 0;
            var stopwatch = Stopwatch.StartNew();
            var decodeElapsed = TimeSpan.Zero;
            var recognitionElapsed = TimeSpan.Zero;

            for (var sourceFrameIndex = 0; sourceFrameIndex < timeline.FrameCount; sourceFrameIndex++)
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
                    var recognition = await pipeline.ProcessAsync(frame, cancellationToken).ConfigureAwait(false);
                    frames.Add(CreateAnalyzedFrame(sourceFrameIndex, position, recognition, tracks.Update(recognition)));
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
                timeline.Duration,
                timeline.FrameRate,
                timeline.FrameCount,
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
        FrameRecognition recognition,
        IReadOnlyList<ConfirmedPlate> confirmations) => new(
            sourceFrameIndex,
            position,
            recognition.Observations.Select(static observation => new AnalyzedPlateRead(
                observation.Read.Text,
                observation.Read.Confidence,
                observation.Detection.Confidence,
                observation.Detection.Bounds)).ToArray(),
            confirmations.Select(static confirmation => new AnalyzedPlateConfirmation(
                confirmation.Consensus.NormalizedPlate,
                confirmation.Consensus.DisplayPlate,
                confirmation.Consensus.Confidence,
                confirmation.Consensus.ObservationCount,
                confirmation.LastBounds)).ToArray(),
            recognition.SourceWidth,
            recognition.SourceHeight);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        (pipeline as IDisposable)?.Dispose();
        _runGate.Dispose();
    }
}