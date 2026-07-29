using System.Diagnostics;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Inference;

/// <summary>
/// Applies recognition, geometry-aware track lifecycle, association, and temporal
/// consensus to an ordered frame stream. Live capture and offline analysis both
/// use this class so a given frame sequence has identical recognition semantics.
/// </summary>
public sealed class RecognitionStreamProcessor : IDisposable
{
    private readonly IFrameRecognitionPipeline _pipeline;
    private readonly PlateTrackManager _tracks;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _rotationDegrees = -1;
    private bool _disposed;

    public RecognitionStreamProcessor(
        IFrameRecognitionPipeline pipeline,
        TrackingOptions? trackingOptions = null,
        ConsensusOptions? consensusOptions = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _tracks = new PlateTrackManager(trackingOptions, consensusOptions);
    }

    public async ValueTask<RecognitionStreamResult> ProcessAsync(
        Yuv420Frame frame,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var recognition = await _pipeline.ProcessAsync(frame, cancellationToken).ConfigureAwait(false);
        ResetForGeometryChange(recognition);

        var trackingStartedAt = Stopwatch.GetTimestamp();
        var tracking = _tracks.UpdateDetailed(recognition);
        var trackingMilliseconds = Stopwatch.GetElapsedTime(trackingStartedAt).TotalMilliseconds;
        return new RecognitionStreamResult(
            recognition,
            tracking.Confirmations,
            new RecognitionStreamDiagnostics(
                recognition.Diagnostics,
                trackingMilliseconds,
                tracking.Tracks,
                tracking.Associations));
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _tracks.Reset();
        _sourceWidth = 0;
        _sourceHeight = 0;
        _rotationDegrees = -1;
    }

    private void ResetForGeometryChange(FrameRecognition recognition)
    {
        if (recognition.SourceWidth == _sourceWidth
            && recognition.SourceHeight == _sourceHeight
            && recognition.RotationDegrees == _rotationDegrees)
        {
            return;
        }

        _tracks.Reset();
        _sourceWidth = recognition.SourceWidth;
        _sourceHeight = recognition.SourceHeight;
        _rotationDegrees = recognition.RotationDegrees;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        (_pipeline as IDisposable)?.Dispose();
    }
}
