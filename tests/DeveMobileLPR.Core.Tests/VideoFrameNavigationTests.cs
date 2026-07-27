using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Core.Tests;

public sealed class VideoFrameNavigationTests
{
    private static readonly IReadOnlyList<AnalyzedVideoFrame> Frames =
    [
        Frame(0),
        Frame(2),
        Frame(5)
    ];

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(1.1, 1)]
    [InlineData(3.5, 1)]
    [InlineData(4, 2)]
    [InlineData(8, 2)]
    public void FindClosestFrameIndex_SnapsToNearestAnalyzedFrame(double seconds, int expectedIndex)
    {
        var index = VideoFrameNavigation.FindClosestFrameIndex(Frames, TimeSpan.FromSeconds(seconds));

        Assert.Equal(expectedIndex, index);
    }

    [Fact]
    public void FindClosestFrameIndex_RejectsEmptyFrameList()
    {
        Assert.Throws<ArgumentException>(() => VideoFrameNavigation.FindClosestFrameIndex([], TimeSpan.Zero));
    }

    private static AnalyzedVideoFrame Frame(double seconds) =>
        new(0, TimeSpan.FromSeconds(seconds), [], []);
}