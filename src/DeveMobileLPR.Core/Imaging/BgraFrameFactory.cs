using System.Buffers;

namespace DeveMobileLPR.Imaging;

public static class BgraFrameFactory
{
    public static Yuv420Frame Create(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        long sequence,
        DateTimeOffset capturedAt,
        int rotationDegrees = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (stride < checked(width * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(stride));
        }
        if (bgra.Length < checked(stride * height))
        {
            throw new ArgumentException("The BGRA source does not contain a complete frame.", nameof(bgra));
        }

        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        var yLength = checked(width * height);
        var chromaLength = checked(chromaWidth * chromaHeight);
        var yOwner = MemoryPool<byte>.Shared.Rent(yLength);
        var uOwner = MemoryPool<byte>.Shared.Rent(chromaLength);
        var vOwner = MemoryPool<byte>.Shared.Rent(chromaLength);
        try
        {
            var yPlane = yOwner.Memory.Span[..yLength];
            var uPlane = uOwner.Memory.Span[..chromaLength];
            var vPlane = vOwner.Memory.Span[..chromaLength];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    GetRgb(bgra, stride, x, y, out var red, out var green, out var blue);
                    yPlane[y * width + x] = YuvColor.Luma(red, green, blue);
                }
            }

            for (var chromaY = 0; chromaY < chromaHeight; chromaY++)
            {
                for (var chromaX = 0; chromaX < chromaWidth; chromaX++)
                {
                    var red = 0;
                    var green = 0;
                    var blue = 0;
                    var samples = 0;
                    for (var offsetY = 0; offsetY < 2; offsetY++)
                    {
                        var sourceY = chromaY * 2 + offsetY;
                        if (sourceY >= height) continue;
                        for (var offsetX = 0; offsetX < 2; offsetX++)
                        {
                            var sourceX = chromaX * 2 + offsetX;
                            if (sourceX >= width) continue;
                            GetRgb(bgra, stride, sourceX, sourceY, out var r, out var g, out var b);
                            red += r;
                            green += g;
                            blue += b;
                            samples++;
                        }
                    }

                    var index = chromaY * chromaWidth + chromaX;
                    uPlane[index] = YuvColor.ChromaBlue(red / samples, green / samples, blue / samples);
                    vPlane[index] = YuvColor.ChromaRed(red / samples, green / samples, blue / samples);
                }
            }

            var frame = new Yuv420Frame(
                sequence, capturedAt, width, height, rotationDegrees,
                yOwner, yLength, width, 1,
                uOwner, chromaLength, chromaWidth, 1,
                vOwner, chromaLength, chromaWidth, 1);
            yOwner = null!;
            uOwner = null!;
            vOwner = null!;
            return frame;
        }
        finally
        {
            yOwner?.Dispose();
            uOwner?.Dispose();
            vOwner?.Dispose();
        }
    }

    private static void GetRgb(
        ReadOnlySpan<byte> bgra,
        int stride,
        int x,
        int y,
        out int red,
        out int green,
        out int blue)
    {
        var offset = checked(y * stride + x * 4);
        blue = bgra[offset];
        green = bgra[offset + 1];
        red = bgra[offset + 2];
    }
}

internal static class YuvColor
{
    public static byte Luma(int red, int green, int blue) =>
        Clamp(((66 * red + 129 * green + 25 * blue + 128) >> 8) + 16);

    public static byte ChromaBlue(int red, int green, int blue) =>
        Clamp(((-38 * red - 74 * green + 112 * blue + 128) >> 8) + 128);

    public static byte ChromaRed(int red, int green, int blue) =>
        Clamp(((112 * red - 94 * green - 18 * blue + 128) >> 8) + 128);

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
