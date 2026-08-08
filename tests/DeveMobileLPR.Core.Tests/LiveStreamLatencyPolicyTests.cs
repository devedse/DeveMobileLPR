using DeveMobileLPR.Streaming;

namespace DeveMobileLPR.Tests;

public sealed class LiveStreamLatencyPolicyTests
{
    [Fact]
    public void ShouldResync_UsesBudgetAndMinimumInterval()
    {
        var policy = new LiveStreamLatencyPolicy(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(10));
        var now = DateTimeOffset.UnixEpoch;

        Assert.False(policy.ShouldResync(TimeSpan.FromSeconds(3), now));
        Assert.True(policy.ShouldResync(TimeSpan.FromSeconds(4), now));
        Assert.False(policy.ShouldResync(TimeSpan.FromSeconds(4), now.AddSeconds(9)));
        Assert.True(policy.ShouldResync(TimeSpan.FromSeconds(4), now.AddSeconds(10)));
    }
}
