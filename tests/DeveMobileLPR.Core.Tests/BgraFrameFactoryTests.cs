using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Core.Tests;

public sealed class BgraFrameFactoryTests
{
    [Fact]
    public void CreateMatchesArgbConversionIncludingAveragedChroma()
    {
        var argb = new[]
        {
            unchecked((int)0xffff0000), unchecked((int)0xff00ff00),
            unchecked((int)0xff0000ff), unchecked((int)0xffffffff)
        };
        var bgra = new byte[]
        {
            0, 0, 255, 255, 0, 255, 0, 255,
            255, 0, 0, 255, 255, 255, 255, 255
        };

        using var expected = ArgbFrameFactory.Create(argb, 2, 2, 1, DateTimeOffset.UnixEpoch);
        using var actual = BgraFrameFactory.Create(bgra, 2, 2, 8, 1, DateTimeOffset.UnixEpoch);

        Assert.Equal(expected.YPlane.ToArray(), actual.YPlane.ToArray());
        Assert.Equal(expected.UPlane.ToArray(), actual.UPlane.ToArray());
        Assert.Equal(expected.VPlane.ToArray(), actual.VPlane.ToArray());
    }

    [Fact]
    public void CreateHonorsStridePaddingAndOddDimensions()
    {
        const int width = 3;
        const int height = 3;
        const int stride = 16;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                pixels[offset + 2] = 255;
                pixels[offset + 3] = 255;
            }
        }

        using var frame = BgraFrameFactory.Create(
            pixels, width, height, stride, 3, DateTimeOffset.UnixEpoch, rotationDegrees: 90);

        Assert.Equal(9, frame.YLength);
        Assert.Equal(4, frame.ULength);
        Assert.Equal(4, frame.VLength);
        Assert.All(frame.YPlane.ToArray(), value => Assert.Equal((byte)82, value));
        Assert.All(frame.UPlane.ToArray(), value => Assert.Equal((byte)90, value));
        Assert.All(frame.VPlane.ToArray(), value => Assert.Equal((byte)240, value));
        Assert.Equal(90, frame.RotationDegrees);
    }
}
