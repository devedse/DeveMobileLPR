namespace DeveMobileLPR.Recognition;

public readonly record struct VideoFrameSampling(int Interval)
{
    public static VideoFrameSampling AllFrames => new(1);

    public bool Includes(long frameIndex)
    {
        if (Interval < 1)
        {
            throw new InvalidOperationException("The video frame interval must be at least one.");
        }

        return frameIndex >= 0 && frameIndex % Interval == 0;
    }
}

public readonly record struct VideoFrameTimeline(TimeSpan Duration, double FrameRate, int FrameCount)
{
    public static VideoFrameTimeline Create(TimeSpan duration, double? reportedFrameRate, int? reportedFrameCount)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var frameRate = reportedFrameRate is > 0
            ? reportedFrameRate.Value
            : reportedFrameCount is > 0
                ? reportedFrameCount.Value / duration.TotalSeconds
                : 30d;
        var frameCount = reportedFrameCount is > 0
            ? reportedFrameCount.Value
            : checked((int)Math.Ceiling(duration.TotalSeconds * frameRate));
        return new VideoFrameTimeline(duration, frameRate, Math.Max(1, frameCount));
    }

    public TimeSpan PositionOf(long frameIndex) => TimeSpan.FromSeconds(frameIndex / FrameRate);
}