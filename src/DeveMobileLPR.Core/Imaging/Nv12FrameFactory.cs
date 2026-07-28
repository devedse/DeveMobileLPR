using System.Buffers;

namespace DeveMobileLPR.Imaging;

public static class Nv12FrameFactory
{
    public static Yuv420Frame Create(
        ReadOnlySpan<byte> source,
        int sourceStride,
        int width,
        int height,
        long sequence,
        DateTimeOffset capturedAt,
        int rotationDegrees)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (sourceStride < checked(((width + 1) / 2) * 2))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStride));
        }

        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        var yLength = checked(width * height);
        var chromaLength = checked(chromaWidth * chromaHeight);
        var requiredLength = checked(sourceStride * (height + chromaHeight));
        if (source.Length < requiredLength)
        {
            throw new ArgumentException(
                $"NV12 source is too small: {source.Length} bytes; expected at least {requiredLength}.",
                nameof(source));
        }

        var yOwner = MemoryPool<byte>.Shared.Rent(yLength);
        var uOwner = MemoryPool<byte>.Shared.Rent(chromaLength);
        var vOwner = MemoryPool<byte>.Shared.Rent(chromaLength);
        try
        {
            var yPlane = yOwner.Memory.Span[..yLength];
            var uPlane = uOwner.Memory.Span[..chromaLength];
            var vPlane = vOwner.Memory.Span[..chromaLength];
            for (var row = 0; row < height; row++)
            {
                source.Slice(row * sourceStride, width).CopyTo(yPlane.Slice(row * width, width));
            }

            var chromaStart = sourceStride * height;
            for (var row = 0; row < chromaHeight; row++)
            {
                var sourceRow = source.Slice(chromaStart + row * sourceStride, sourceStride);
                var destinationOffset = row * chromaWidth;
                for (var column = 0; column < chromaWidth; column++)
                {
                    uPlane[destinationOffset + column] = sourceRow[column * 2];
                    vPlane[destinationOffset + column] = sourceRow[column * 2 + 1];
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
}
