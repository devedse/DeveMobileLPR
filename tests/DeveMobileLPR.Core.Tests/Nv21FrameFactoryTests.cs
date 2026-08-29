using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Core.Tests;

public sealed class Nv21FrameFactoryTests
{
    [Fact]
    public void Create_SplitsInterleavedVuAndRemovesPadding()
    {
        byte[] source =
        [
            1, 2, 3, 4, 99, 99,
            5, 6, 7, 8, 99, 99,
            9, 10, 11, 12, 99, 99,
            13, 14, 15, 16, 99, 99,
            201, 101, 202, 102, 99, 99,
            203, 103, 204, 104, 99, 99
        ];

        using var frame = Nv21FrameFactory.Create(source, 6, 4, 4, 7, DateTimeOffset.UnixEpoch, 0);

        Assert.Equal(source.AsSpan(0, 4).ToArray(), frame.YPlane.Span[..4].ToArray());
        Assert.Equal(new byte[] { 101, 102, 103, 104 }, frame.UPlane.Span.ToArray());
        Assert.Equal(new byte[] { 201, 202, 203, 204 }, frame.VPlane.Span.ToArray());
        Assert.Equal(4, frame.YRowStride);
        Assert.Equal(2, frame.URowStride);
        Assert.Equal(2, frame.VRowStride);
    }

    [Fact]
    public void Create_RejectsShortSource()
    {
        Assert.Throws<ArgumentException>(() =>
            Nv21FrameFactory.Create(new byte[23], 4, 4, 4, 1, DateTimeOffset.UtcNow, 0));
    }

    [Fact]
    public void CreateCropped_ReturnsCenteredChromaAlignedRegion()
    {
        byte[] source =
        [
            1, 2, 3, 4, 5, 6,
            7, 8, 9, 10, 11, 12,
            13, 14, 15, 16, 17, 18,
            19, 20, 21, 22, 23, 24,
            201, 101, 202, 102, 203, 103,
            204, 104, 205, 105, 206, 106
        ];

        using var frame = Nv21FrameFactory.CreateCropped(
            source, 6, 6, 4, 2, 0, 4, 4, 8, DateTimeOffset.UnixEpoch, 0);

        Assert.Equal(new byte[] { 3, 4, 5, 6, 9, 10, 11, 12, 15, 16, 17, 18, 21, 22, 23, 24 }, frame.YPlane.Span.ToArray());
        Assert.Equal(new byte[] { 102, 103, 105, 106 }, frame.UPlane.Span.ToArray());
        Assert.Equal(new byte[] { 202, 203, 205, 206 }, frame.VPlane.Span.ToArray());
    }
}
