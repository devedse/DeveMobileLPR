using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Core.Tests;

public sealed class BiPlanarNv12FrameFactoryTests
{
    [Fact]
    public void Create_CopiesPaddedPlanesAndSplitsInterleavedChroma()
    {
        byte[] luma = [1, 2, 99, 99, 3, 4, 99, 99];
        byte[] chroma = [10, 20, 99, 99];

        using var frame = BiPlanarNv12FrameFactory.Create(
            luma, 4, chroma, 4, 2, 2, 7, DateTimeOffset.UnixEpoch);

        Assert.Equal([1, 2, 3, 4], frame.YPlane.ToArray());
        Assert.Equal(10, frame.UPlane.Span[0]);
        Assert.Equal(20, frame.VPlane.Span[0]);
        Assert.Equal(7, frame.Sequence);
    }

    [Fact]
    public void Create_RejectsAnIncompletePlane()
    {
        Assert.Throws<ArgumentException>(() => BiPlanarNv12FrameFactory.Create(
            new byte[3], 2, new byte[2], 2, 2, 2, 1, DateTimeOffset.UnixEpoch));
    }
}
