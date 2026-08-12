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
}
