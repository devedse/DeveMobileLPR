using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Tests;

public sealed class MaximumWeightBipartiteMatcherTests
{
    [Fact]
    public void Match_FindsGlobalOptimumInsteadOfGreedyChoice()
    {
        float?[,] scores =
        {
            { 0.9f, 0.7f },
            { 0.8f, null }
        };

        var matches = MaximumWeightBipartiteMatcher.Match(scores);

        Assert.Contains((0, 1), matches);
        Assert.Contains((1, 0), matches);
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void Match_LeavesRowsWithoutEligibleTracksUnmatched()
    {
        float?[,] scores =
        {
            { null, 0.4f },
            { null, null }
        };

        var match = Assert.Single(MaximumWeightBipartiteMatcher.Match(scores));

        Assert.Equal((0, 1), match);
    }

    [Fact]
    public void Match_DoesNotAssignOneTrackToMultipleRows()
    {
        float?[,] scores =
        {
            { 0.9f },
            { 0.8f },
            { 0.7f }
        };

        var match = Assert.Single(MaximumWeightBipartiteMatcher.Match(scores));

        Assert.Equal((0, 0), match);
    }
}
