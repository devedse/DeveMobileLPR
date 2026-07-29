using System.Buffers;

namespace DeveMobileLPR.Imaging;

/// <summary>
/// Creates a tightly packed YUV420 frame from Android/Java ARGB pixels. This is
/// used when a platform decoder exposes its displayed texture as RGB while the
/// recognition pipeline expects the same YUV representation as camera input.
/// </summary>
public static class ArgbFrameFactory
{
    public static Yuv420Frame Create(
        ReadOnlySpan<int> argb,
        int width,
        int height,
        long sequence,
        DateTimeOffset capturedAt,
        int rotationDegrees = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (argb.Length < checked(width * height))
        {
            throw new ArgumentException("The ARGB source does not contain a complete frame.", nameof(argb));
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
                    var pixel = argb[y * width + x];
                    GetRgb(pixel, out var red, out var green, out var blue);
                    yPlane[y * width + x] = Luma(red, green, blue);
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
                        if (sourceY >= height)
                        {
                            continue;
                        }

                        for (var offsetX = 0; offsetX < 2; offsetX++)
                        {
                            var sourceX = chromaX * 2 + offsetX;
                            if (sourceX >= width)
                            {
                                continue;
                            }

                            GetRgb(argb[sourceY * width + sourceX], out var r, out var g, out var b);
                            red += r;
                            green += g;
                            blue += b;
                            samples++;
                        }
                    }

                    var index = chromaY * chromaWidth + chromaX;
                    uPlane[index] = ChromaBlue(red / samples, green / samples, blue / samples);
                    vPlane[index] = ChromaRed(red / samples, green / samples, blue / samples);
                }
            }

            var frame = new Yuv420Frame(
                sequence,
                capturedAt,
                width,
                height,
                rotationDegrees,
                yOwner,
                yLength,
                width,
                1,
                uOwner,
                chromaLength,
                chromaWidth,
                1,
                vOwner,
                chromaLength,
                chromaWidth,
                1);
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

    private static void GetRgb(int argb, out int red, out int green, out int blue)
    {
        red = (argb >> 16) & 0xff;
        green = (argb >> 8) & 0xff;
        blue = argb & 0xff;
    }

    private static byte Luma(int red, int green, int blue) =>
        Clamp(((66 * red + 129 * green + 25 * blue + 128) >> 8) + 16);

    private static byte ChromaBlue(int red, int green, int blue) =>
        Clamp(((-38 * red - 74 * green + 112 * blue + 128) >> 8) + 128);

    private static byte ChromaRed(int red, int green, int blue) =>
        Clamp(((112 * red - 94 * green - 18 * blue + 128) >> 8) + 128);

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
