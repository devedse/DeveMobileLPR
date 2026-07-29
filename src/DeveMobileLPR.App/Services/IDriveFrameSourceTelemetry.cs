namespace DeveMobileLPR.App.Services;

/// <summary>
/// Reports distinct stages of a drive video pipeline. Source frames are frames
/// delivered by a camera or decoder; preview frames have actually reached an
/// application-owned presentation surface.
/// </summary>
internal interface IDriveFrameSourceTelemetry
{
    event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
    event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented;

    /// <summary>
    /// True only when <see cref="PreviewFramesPresented"/> is backed by a real
    /// presentation callback rather than an estimate based on source frames.
    /// </summary>
    bool ReportsPreviewFrames { get; }
}

/// <summary>
/// Carries a batch of frames so sources backed by cumulative decoder counters
/// do not need to synthesize one managed event per decoded frame.
/// </summary>
internal sealed class DriveFrameCountEventArgs(long count) : EventArgs
{
    public long Count { get; } = count > 0
        ? count
        : throw new ArgumentOutOfRangeException(nameof(count));
}
