using System.Buffers;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference.Preprocessing;

namespace DeveMobileLPR.Inference.Tests;

public sealed class YuvImageSamplerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void CachedSamplerMatchesFrameBasedBilinearReference(int rotationDegrees)
    {
        using var frame = CreateFrame(rotationDegrees);
        var sampler = new YuvImageSampler(frame);
        var points = new[]
        {
            (0f, 0f),
            (0.25f, 0.75f),
            (frame.OrientedWidth / 2f + 0.2f, frame.OrientedHeight / 2f - 0.35f),
            (frame.OrientedWidth - 1.1f, frame.OrientedHeight - 1.2f),
            (frame.OrientedWidth - 1f, frame.OrientedHeight - 1f)
        };

        foreach (var (x, y) in points)
        {
            sampler.SampleBilinear(x, y, out var actualRed, out var actualGreen, out var actualBlue);
            SampleReference(frame, x, y, out var expectedRed, out var expectedGreen, out var expectedBlue);

            Assert.Equal(expectedRed, actualRed);
            Assert.Equal(expectedGreen, actualGreen);
            Assert.Equal(expectedBlue, actualBlue);
        }
    }

    private static Yuv420Frame CreateFrame(int rotationDegrees)
    {
        const int width = 6;
        const int height = 4;
        var y = MemoryPool<byte>.Shared.Rent(width * height);
        var u = MemoryPool<byte>.Shared.Rent(width * height / 4);
        var v = MemoryPool<byte>.Shared.Rent(width * height / 4);
        for (var index = 0; index < width * height; index++)
        {
            y.Memory.Span[index] = (byte)(32 + index * 7);
        }
        for (var index = 0; index < width * height / 4; index++)
        {
            u.Memory.Span[index] = (byte)(88 + index * 9);
            v.Memory.Span[index] = (byte)(168 - index * 8);
        }

        return new Yuv420Frame(
            1,
            DateTimeOffset.UnixEpoch,
            width,
            height,
            rotationDegrees,
            y,
            width * height,
            width,
            1,
            u,
            width * height / 4,
            width / 2,
            1,
            v,
            width * height / 4,
            width / 2,
            1);
    }

    private static void SampleReference(
        Yuv420Frame frame,
        float x,
        float y,
        out byte red,
        out byte green,
        out byte blue)
    {
        var x0 = Math.Clamp((int)MathF.Floor(x), 0, frame.OrientedWidth - 1);
        var y0 = Math.Clamp((int)MathF.Floor(y), 0, frame.OrientedHeight - 1);
        var x1 = Math.Min(x0 + 1, frame.OrientedWidth - 1);
        var y1 = Math.Min(y0 + 1, frame.OrientedHeight - 1);
        var wx = Math.Clamp(x - x0, 0, 1);
        var wy = Math.Clamp(y - y0, 0, 1);

        frame.GetRgb(x0, y0, out var r00, out var g00, out var b00);
        frame.GetRgb(x1, y0, out var r10, out var g10, out var b10);
        frame.GetRgb(x0, y1, out var r01, out var g01, out var b01);
        frame.GetRgb(x1, y1, out var r11, out var g11, out var b11);

        red = Interpolate(r00, r10, r01, r11, wx, wy);
        green = Interpolate(g00, g10, g01, g11, wx, wy);
        blue = Interpolate(b00, b10, b01, b11, wx, wy);
    }

    private static byte Interpolate(byte topLeft, byte topRight, byte bottomLeft, byte bottomRight, float x, float y)
    {
        var top = topLeft + (topRight - topLeft) * x;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * x;
        return (byte)Math.Clamp((int)MathF.Round(top + (bottom - top) * y), 0, 255);
    }
}
