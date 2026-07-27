using System.Buffers;
using DeveMobileLPR.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DeveMobileLPR.App.Platforms.Windows;

internal static class WindowsSoftwareBitmapConverter
{
    public static Yuv420Frame ToYuv420Frame(SoftwareBitmap bitmap, long sequence, DateTimeOffset timestamp)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var pixelCount = checked(width * height);
        var bgraLength = checked(pixelCount * 4);
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        var chromaLength = checked(chromaWidth * chromaHeight);
        var buffer = new global::Windows.Storage.Streams.Buffer(checked((uint)bgraLength));
        bitmap.CopyToBuffer(buffer);
        var pixels = new byte[bgraLength];
        var yOwner = MemoryPool<byte>.Shared.Rent(pixelCount);
        var uOwner = MemoryPool<byte>.Shared.Rent(chromaLength);
        var vOwner = MemoryPool<byte>.Shared.Rent(chromaLength);
        try
        {
            using var reader = DataReader.FromBuffer(buffer);
            reader.ReadBytes(pixels);
            var yPlane = yOwner.Memory.Span[..pixelCount];
            var uPlane = uOwner.Memory.Span[..chromaLength];
            var vPlane = vOwner.Memory.Span[..chromaLength];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = (y * width + x) * 4;
                    var blue = pixels[offset];
                    var green = pixels[offset + 1];
                    var red = pixels[offset + 2];
                    yPlane[y * width + x] = Clamp((66 * red + 129 * green + 25 * blue + 128 >> 8) + 16);
                }
            }

            for (var y = 0; y < height; y += 2)
            {
                for (var x = 0; x < width; x += 2)
                {
                    var offset = (y * width + x) * 4;
                    var blue = pixels[offset];
                    var green = pixels[offset + 1];
                    var red = pixels[offset + 2];
                    var chromaIndex = y / 2 * chromaWidth + x / 2;
                    uPlane[chromaIndex] = Clamp((-38 * red - 74 * green + 112 * blue + 128 >> 8) + 128);
                    vPlane[chromaIndex] = Clamp((112 * red - 94 * green - 18 * blue + 128 >> 8) + 128);
                }
            }

            var frame = new Yuv420Frame(
                sequence,
                timestamp,
                width,
                height,
                0,
                yOwner,
                pixelCount,
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

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}