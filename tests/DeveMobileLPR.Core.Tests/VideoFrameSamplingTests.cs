using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Core.Tests;

public sealed class VideoFrameSamplingTests
{
    [Fact]
    public void AllFrames_IncludesEveryNonNegativeFrame()
    {
        var sampling = VideoFrameSampling.AllFrames;

        Assert.All(Enumerable.Range(0, 12), frameIndex => Assert.True(sampling.Includes(frameIndex)));
    }

    [Fact]
    public void Includes_UsesZeroBasedFrameInterval()
    {
        var sampling = new VideoFrameSampling(4);

        Assert.True(sampling.Includes(0));
        Assert.False(sampling.Includes(1));
        Assert.True(sampling.Includes(4));
        Assert.True(sampling.Includes(8));
    }

    [Fact]
    public void Includes_RejectsInvalidInterval()
    {
        var sampling = new VideoFrameSampling(0);

        Assert.Throws<InvalidOperationException>(() => sampling.Includes(0));
    }

    [Fact]
    public void Timeline_DerivesFrameRateFromFrameCountAndDuration()
    {
        var timeline = VideoFrameTimeline.Create(TimeSpan.FromSeconds(2), null, 16);

        Assert.Equal(8, timeline.FrameRate);
        Assert.Equal(TimeSpan.FromSeconds(0.5), timeline.PositionOf(4));
    }

    [Fact]
    public void Timeline_UsesReportedFrameRateWhenFrameCountIsMissing()
    {
        var timeline = VideoFrameTimeline.Create(TimeSpan.FromSeconds(2), 12, null);

        Assert.Equal(24, timeline.FrameCount);
        Assert.Equal(TimeSpan.FromSeconds(1), timeline.PositionOf(12));
    }
}