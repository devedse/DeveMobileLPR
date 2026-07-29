using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Core.Tests;

public sealed class ArgbFrameFactoryTests
{
    [Fact]
    public void CreatePreservesGeometryAndProducesExpectedNeutralChroma()
    {
        var pixels = new[]
        {
            unchecked((int)0xff000000), unchecked((int)0xffffffff),
            unchecked((int)0xff808080), unchecked((int)0xff404040)
        };

        using var frame = ArgbFrameFactory.Create(pixels, 2, 2, 7, DateTimeOffset.UnixEpoch);

        Assert.Equal(2, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(7, frame.Sequence);
        Assert.Equal(new byte[] { 16, 235, 126, 71 }, frame.YPlane.ToArray());
        Assert.Equal(new byte[] { 128 }, frame.UPlane.ToArray());
        Assert.Equal(new byte[] { 128 }, frame.VPlane.ToArray());
    }

    [Fact]
    public void CreateSupportsOddDimensions()
    {
        var pixels = Enumerable.Repeat(unchecked((int)0xffff0000), 9).ToArray();

        using var frame = ArgbFrameFactory.Create(pixels, 3, 3, 1, DateTimeOffset.UnixEpoch);

        Assert.Equal(9, frame.YLength);
        Assert.Equal(4, frame.ULength);
        Assert.Equal(4, frame.VLength);
    }

    [Fact]
    public void CreateConvertsColorChromaAndPreservesRotation()
    {
        var pixels = Enumerable.Repeat(unchecked((int)0xffff0000), 4).ToArray();

        using var frame = ArgbFrameFactory.Create(
            pixels,
            2,
            2,
            1,
            DateTimeOffset.UnixEpoch,
            rotationDegrees: 90);

        Assert.Equal(90, frame.RotationDegrees);
        Assert.Equal(2, frame.OrientedWidth);
        Assert.Equal(2, frame.OrientedHeight);
        Assert.Equal(new byte[] { 82, 82, 82, 82 }, frame.YPlane.ToArray());
        Assert.Equal(new byte[] { 90 }, frame.UPlane.ToArray());
        Assert.Equal(new byte[] { 240 }, frame.VPlane.ToArray());
    }
}
