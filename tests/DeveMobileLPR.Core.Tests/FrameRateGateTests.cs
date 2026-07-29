using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Tests;

public sealed class FrameRateGateTests
{
    [Fact]
    public void TryAcquire_EnforcesConfiguredMaximum()
    {
        var gate = new FrameRateGate(timestampFrequency: 1000);

        Assert.True(gate.TryAcquire(timestamp: 100, maximumFramesPerSecond: 4));
        Assert.False(gate.TryAcquire(timestamp: 349, maximumFramesPerSecond: 4));
        Assert.True(gate.TryAcquire(timestamp: 350, maximumFramesPerSecond: 4));
    }

    [Fact]
    public void TryAcquire_UnlimitedAcceptsEveryTimestamp()
    {
        var gate = new FrameRateGate(timestampFrequency: 1000);

        Assert.True(gate.TryAcquire(timestamp: 100, maximumFramesPerSecond: 0));
        Assert.True(gate.TryAcquire(timestamp: 100, maximumFramesPerSecond: 0));
        Assert.True(gate.TryAcquire(timestamp: 101, maximumFramesPerSecond: 0));
    }

    [Fact]
    public void TryAcquire_ChangedMaximumTakesEffectImmediately()
    {
        var gate = new FrameRateGate(timestampFrequency: 1000);

        Assert.True(gate.TryAcquire(timestamp: 0, maximumFramesPerSecond: 4));
        Assert.False(gate.TryAcquire(timestamp: 100, maximumFramesPerSecond: 4));
        Assert.True(gate.TryAcquire(timestamp: 100, maximumFramesPerSecond: 8));
    }

    [Fact]
    public void TryAcquire_RoundsIntervalUpToHonorMaximum()
    {
        var gate = new FrameRateGate(timestampFrequency: 1000);

        Assert.True(gate.TryAcquire(timestamp: 0, maximumFramesPerSecond: 12));
        Assert.False(gate.TryAcquire(timestamp: 83, maximumFramesPerSecond: 12));
        Assert.True(gate.TryAcquire(timestamp: 84, maximumFramesPerSecond: 12));
    }

    [Fact]
    public void TryAcquire_PreservesAverageRateForDiscreteSourceFrames()
    {
        var gate = new FrameRateGate(timestampFrequency: 1000);

        var accepted = Enumerable.Range(0, 20)
            .Select(index => index * 50L)
            .Count(timestamp => gate.TryAcquire(timestamp, maximumFramesPerSecond: 8));

        Assert.Equal(8, accepted);
    }

    [Fact]
    public void Reset_AllowsTimestampDomainToRestart()
    {
        var gate = new FrameRateGate(timestampFrequency: TimeSpan.TicksPerSecond);

        Assert.True(gate.TryAcquire(TimeSpan.TicksPerSecond, maximumFramesPerSecond: 4));
        gate.Reset();

        Assert.True(gate.TryAcquire(timestamp: 0, maximumFramesPerSecond: 4));
    }
}
