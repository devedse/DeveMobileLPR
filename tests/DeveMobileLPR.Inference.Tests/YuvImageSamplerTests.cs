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
    public void SampleBilinear_MatchesYuvFirstReference(int rotationDegrees)
    {
        foreach (var paddedPlanes in new[] { false, true })
        {
            using var frame = CreateFrame(rotationDegrees, paddedPlanes);
            var sampler = new YuvImageSampler(frame);

            foreach (var (x, y) in SamplePoints(frame))
            {
                sampler.SampleBilinear(x, y, out var actualRed, out var actualGreen, out var actualBlue);
                SampleYuvFirstReference(frame, x, y, out var expectedRed, out var expectedGreen, out var expectedBlue);

                Assert.Equal(expectedRed, actualRed);
                Assert.Equal(expectedGreen, actualGreen);
                Assert.Equal(expectedBlue, actualBlue);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void SampleBilinear_RemainsCloseToRgbFirstReference(int rotationDegrees)
    {
        using var frame = CreateFrame(rotationDegrees, paddedPlanes: true);
        var sampler = new YuvImageSampler(frame);

        foreach (var (x, y) in SamplePoints(frame))
        {
            sampler.SampleBilinear(x, y, out var actualRed, out var actualGreen, out var actualBlue);
            SampleRgbFirstReference(frame, x, y, out var expectedRed, out var expectedGreen, out var expectedBlue);

            Assert.InRange(Math.Abs(expectedRed - actualRed), 0, 4);
            Assert.InRange(Math.Abs(expectedGreen - actualGreen), 0, 4);
            Assert.InRange(Math.Abs(expectedBlue - actualBlue), 0, 4);
        }
    }

    private static Yuv420Frame CreateFrame(int rotationDegrees, bool paddedPlanes)
    {
        const int width = 6;
        const int height = 4;
        var yRowStride = paddedPlanes ? 8 : width;
        var yPixelStride = 1;
        var chromaRowStride = paddedPlanes ? 8 : width / 2;
        var chromaPixelStride = paddedPlanes ? 2 : 1;
        var yLength = yRowStride * height;
        var chromaLength = chromaRowStride * (height / 2);
        var y = MemoryPool<byte>.Shared.Rent(yLength);
        var u = MemoryPool<byte>.Shared.Rent(chromaLength);
        var v = MemoryPool<byte>.Shared.Rent(chromaLength);
        y.Memory.Span[..yLength].Clear();
        u.Memory.Span[..chromaLength].Clear();
        v.Memory.Span[..chromaLength].Clear();
        for (var rawY = 0; rawY < height; rawY++)
        {
            for (var rawX = 0; rawX < width; rawX++)
            {
                var index = rawY * width + rawX;
                y.Memory.Span[rawY * yRowStride + rawX * yPixelStride] = (byte)(32 + index * 7);
            }
        }
        for (var chromaY = 0; chromaY < height / 2; chromaY++)
        {
            for (var chromaX = 0; chromaX < width / 2; chromaX++)
            {
                var index = chromaY * (width / 2) + chromaX;
                var offset = chromaY * chromaRowStride + chromaX * chromaPixelStride;
                u.Memory.Span[offset] = (byte)(88 + index * 9);
                v.Memory.Span[offset] = (byte)(168 - index * 8);
            }
        }

        return new Yuv420Frame(
            1,
            DateTimeOffset.UnixEpoch,
            width,
            height,
            rotationDegrees,
            y,
            yLength,
            yRowStride,
            yPixelStride,
            u,
            chromaLength,
            chromaRowStride,
            chromaPixelStride,
            v,
            chromaLength,
            chromaRowStride,
            chromaPixelStride);
    }

    private static (float X, float Y)[] SamplePoints(Yuv420Frame frame) =>
    [
        (0f, 0f),
        (0.25f, 0.75f),
        (frame.OrientedWidth / 2f + 0.2f, frame.OrientedHeight / 2f - 0.35f),
        (frame.OrientedWidth - 1.1f, frame.OrientedHeight - 1.2f),
        (frame.OrientedWidth - 1f, frame.OrientedHeight - 1f)
    ];

    private static void SampleYuvFirstReference(
        Yuv420Frame frame,
        float x,
        float y,
        out byte red,
        out byte green,
        out byte blue)
    {
        GetSampleCoordinates(frame, x, y, out var x0, out var y0, out var x1, out var y1, out var wx, out var wy);
        GetYuv(frame, x0, y0, out var y00, out var u00, out var v00);
        GetYuv(frame, x1, y0, out var y10, out var u10, out var v10);
        GetYuv(frame, x0, y1, out var y01, out var u01, out var v01);
        GetYuv(frame, x1, y1, out var y11, out var u11, out var v11);

        var interpolatedY = Interpolate(y00, y10, y01, y11, wx, wy);
        var interpolatedU = Interpolate(u00, u10, u01, u11, wx, wy);
        var interpolatedV = Interpolate(v00, v10, v01, v11, wx, wy);
        Yuv420Frame.ConvertYuvToRgb(interpolatedY, interpolatedU, interpolatedV, out red, out green, out blue);
    }

    private static void SampleRgbFirstReference(
        Yuv420Frame frame,
        float x,
        float y,
        out byte red,
        out byte green,
        out byte blue)
    {
        GetSampleCoordinates(frame, x, y, out var x0, out var y0, out var x1, out var y1, out var wx, out var wy);

        frame.GetRgb(x0, y0, out var r00, out var g00, out var b00);
        frame.GetRgb(x1, y0, out var r10, out var g10, out var b10);
        frame.GetRgb(x0, y1, out var r01, out var g01, out var b01);
        frame.GetRgb(x1, y1, out var r11, out var g11, out var b11);

        red = Interpolate(r00, r10, r01, r11, wx, wy);
        green = Interpolate(g00, g10, g01, g11, wx, wy);
        blue = Interpolate(b00, b10, b01, b11, wx, wy);
    }

    private static void GetSampleCoordinates(
        Yuv420Frame frame,
        float x,
        float y,
        out int x0,
        out int y0,
        out int x1,
        out int y1,
        out float wx,
        out float wy)
    {
        x0 = Math.Clamp((int)MathF.Floor(x), 0, frame.OrientedWidth - 1);
        y0 = Math.Clamp((int)MathF.Floor(y), 0, frame.OrientedHeight - 1);
        x1 = Math.Min(x0 + 1, frame.OrientedWidth - 1);
        y1 = Math.Min(y0 + 1, frame.OrientedHeight - 1);
        wx = Math.Clamp(x - x0, 0, 1);
        wy = Math.Clamp(y - y0, 0, 1);
    }

    private static void GetYuv(Yuv420Frame frame, int orientedX, int orientedY, out byte y, out byte u, out byte v)
    {
        var (rawX, rawY) = frame.RotationDegrees switch
        {
            0 => (orientedX, orientedY),
            90 => (orientedY, frame.Height - 1 - orientedX),
            180 => (frame.Width - 1 - orientedX, frame.Height - 1 - orientedY),
            270 => (frame.Width - 1 - orientedY, orientedX),
            _ => throw new InvalidOperationException()
        };
        var chromaX = rawX / 2;
        var chromaY = rawY / 2;
        y = frame.YPlane.Span[rawY * frame.YRowStride + rawX * frame.YPixelStride];
        u = frame.UPlane.Span[chromaY * frame.URowStride + chromaX * frame.UPixelStride];
        v = frame.VPlane.Span[chromaY * frame.VRowStride + chromaX * frame.VPixelStride];
    }

    private static byte Interpolate(byte topLeft, byte topRight, byte bottomLeft, byte bottomRight, float x, float y)
    {
        var top = topLeft + (topRight - topLeft) * x;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * x;
        return (byte)Math.Clamp((int)MathF.Round(top + (bottom - top) * y), 0, 255);
    }
}
