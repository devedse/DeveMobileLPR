using System.Buffers;
using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Tests;

public sealed class Yuv420FrameTests
{
    [Fact]
    public void ConvertYuvToRgb_ConvertsNeutralWhite()
    {
        Yuv420Frame.ConvertYuvToRgb(235, 128, 128, out var red, out var green, out var blue);
        Assert.InRange(red, 253, 255);
        Assert.InRange(green, 253, 255);
        Assert.InRange(blue, 253, 255);
    }

    [Theory]
    [InlineData(0, 4, 2)]
    [InlineData(90, 2, 4)]
    [InlineData(180, 4, 2)]
    [InlineData(270, 2, 4)]
    public void OrientedDimensions_RespectRotation(int rotation, int expectedWidth, int expectedHeight)
    {
        using var frame = CreateFrame(4, 2, rotation);
        Assert.Equal(expectedWidth, frame.OrientedWidth);
        Assert.Equal(expectedHeight, frame.OrientedHeight);
    }

    internal static Yuv420Frame CreateFrame(int width, int height, int rotation = 0)
    {
        var y = MemoryPool<byte>.Shared.Rent(width * height);
        var u = MemoryPool<byte>.Shared.Rent(Math.Max(1, width * height / 4));
        var v = MemoryPool<byte>.Shared.Rent(Math.Max(1, width * height / 4));
        y.Memory.Span[..(width * height)].Fill(128);
        u.Memory.Span[..Math.Max(1, width * height / 4)].Fill(128);
        v.Memory.Span[..Math.Max(1, width * height / 4)].Fill(128);
        return new Yuv420Frame(
            1,
            DateTimeOffset.UtcNow,
            width,
            height,
            rotation,
            y,
            width * height,
            width,
            1,
            u,
            Math.Max(1, width * height / 4),
            Math.Max(1, width / 2),
            1,
            v,
            Math.Max(1, width * height / 4),
            Math.Max(1, width / 2),
            1);
    }
}
