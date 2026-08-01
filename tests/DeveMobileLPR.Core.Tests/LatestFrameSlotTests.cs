using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Tests;

public sealed class LatestFrameSlotTests
{
    [Fact]
    public async Task ReadAsync_ReturnsNewestFrameAndDisposesReplacedFrames()
    {
        await using var slot = new LatestFrameSlot();
        var first = Yuv420FrameTests.CreateFrame(2, 2);
        var second = Yuv420FrameTests.CreateFrame(2, 2);

        Assert.True(slot.TryWrite(first));
        Assert.True(slot.TryWrite(second));

        Assert.Throws<ObjectDisposedException>(() => _ = first.YPlane);
        Assert.Equal(1, slot.ReplacedFrameCount);
        slot.ResetStatistics();
        Assert.Equal(0, slot.ReplacedFrameCount);
        var result = await slot.ReadAsync(CancellationToken.None);
        Assert.Same(second, result);
        result?.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_UnblocksPendingReaderWithoutAnException()
    {
        var slot = new LatestFrameSlot();
        var pendingRead = slot.ReadAsync(CancellationToken.None).AsTask();

        await slot.DisposeAsync();

        Assert.Null(await pendingRead);
    }

    [Fact]
    public async Task TryWrite_AfterCompletionRejectsAndDisposesFrame()
    {
        var slot = new LatestFrameSlot();
        await slot.DisposeAsync();
        var frame = Yuv420FrameTests.CreateFrame(2, 2);

        Assert.False(slot.TryWrite(frame));
        Assert.Throws<ObjectDisposedException>(() => _ = frame.YPlane);
    }

    [Fact]
    public async Task ConcurrentWriters_CoalesceWithoutLosingTheWakeUp()
    {
        await using var slot = new LatestFrameSlot();
        var writers = Enumerable.Range(1, 250)
            .Select(sequence => Task.Run(() =>
            {
                var frame = Yuv420FrameTests.CreateFrame(2, 2, sequence: sequence);
                slot.TryWrite(frame);
            }))
            .ToArray();

        await Task.WhenAll(writers);
        using var result = await slot.ReadAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.InRange(result.Sequence, 1, 250);
    }
}
