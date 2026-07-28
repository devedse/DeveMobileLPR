using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Tests;

public sealed class Nv12FrameFactoryTests
{
    [Fact]
    public void Create_CopiesPaddedNv12IntoPlanarFrame()
    {
        byte[] source =
        [
            1, 2, 3, 4, 90, 91,
            5, 6, 7, 8, 92, 93,
            10, 20, 11, 21, 94, 95
        ];

        using var frame = Nv12FrameFactory.Create(
            source,
            sourceStride: 6,
            width: 4,
            height: 2,
            sequence: 7,
            capturedAt: DateTimeOffset.UnixEpoch,
            rotationDegrees: 90);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], frame.YPlane.ToArray());
        Assert.Equal([10, 11], frame.UPlane.ToArray());
        Assert.Equal([20, 21], frame.VPlane.ToArray());
        Assert.Equal(7, frame.Sequence);
        Assert.Equal(90, frame.RotationDegrees);
        Assert.Equal(4, frame.YRowStride);
        Assert.Equal(2, frame.URowStride);
    }

    [Fact]
    public void Create_SupportsOddDimensions()
    {
        byte[] source =
        [
            1, 2, 3, 0,
            4, 5, 6, 0,
            7, 8, 9, 0,
            10, 20, 11, 21,
            12, 22, 13, 23
        ];

        using var frame = Nv12FrameFactory.Create(
            source,
            sourceStride: 4,
            width: 3,
            height: 3,
            sequence: 1,
            capturedAt: DateTimeOffset.UnixEpoch,
            rotationDegrees: 0);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], frame.YPlane.ToArray());
        Assert.Equal([10, 11, 12, 13], frame.UPlane.ToArray());
        Assert.Equal([20, 21, 22, 23], frame.VPlane.ToArray());
    }

    [Fact]
    public void Create_RejectsUndersizedSource()
    {
        var exception = Assert.Throws<ArgumentException>(() => Nv12FrameFactory.Create(
            new byte[11],
            sourceStride: 4,
            width: 4,
            height: 2,
            sequence: 1,
            capturedAt: DateTimeOffset.UnixEpoch,
            rotationDegrees: 0));

        Assert.Contains("expected at least 12", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsStrideThatCannotContainChromaRow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Nv12FrameFactory.Create(
            new byte[12],
            sourceStride: 3,
            width: 4,
            height: 2,
            sequence: 1,
            capturedAt: DateTimeOffset.UnixEpoch,
            rotationDegrees: 0));
    }
}