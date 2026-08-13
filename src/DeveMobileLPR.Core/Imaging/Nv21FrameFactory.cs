using System.Buffers;

namespace DeveMobileLPR.Imaging;

/// <summary>Copies packed Android NV21 (Y followed by interleaved VU) into owned YUV420 planes.</summary>
public static class Nv21FrameFactory
{
    public static Yuv420Frame Create(
        ReadOnlySpan<byte> source,
        int sourceStride,
        int width,
        int height,
        long sequence,
        DateTimeOffset capturedAt,
        int rotationDegrees)
        => CreateCropped(
            source,
            sourceStride,
            width,
            height,
            cropX: 0,
            cropY: 0,
            cropWidth: width,
            cropHeight: height,
            sequence,
            capturedAt,
            rotationDegrees);

    public static Yuv420Frame CreateCropped(
        ReadOnlySpan<byte> source,
        int sourceStride,
        int sourceWidth,
        int sourceHeight,
        int cropX,
        int cropY,
        int cropWidth,
        int cropHeight,
        long sequence,
        DateTimeOffset capturedAt,
        int rotationDegrees)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(cropX);
        ArgumentOutOfRangeException.ThrowIfNegative(cropY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cropWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cropHeight);
        if ((cropX & 1) != 0 || (cropY & 1) != 0)
        {
            throw new ArgumentException("NV21 crop coordinates must be even to preserve chroma alignment.");
        }
        if (cropX + cropWidth > sourceWidth || cropY + cropHeight > sourceHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(cropWidth), "The NV21 crop exceeds the source frame.");
        }
        if (sourceStride < checked(((sourceWidth + 1) / 2) * 2))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStride));
        }

        var sourceChromaHeight = (sourceHeight + 1) / 2;
        var chromaWidth = (cropWidth + 1) / 2;
        var chromaHeight = (cropHeight + 1) / 2;
        var yLength = checked(cropWidth * cropHeight);
        var chromaLength = checked(chromaWidth * chromaHeight);
        var requiredLength = checked(sourceStride * (sourceHeight + sourceChromaHeight));
        if (source.Length < requiredLength)
        {
            throw new ArgumentException(
                $"NV21 source is too small: {source.Length} bytes; expected at least {requiredLength}.",
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
            for (var row = 0; row < cropHeight; row++)
            {
                var sourceOffset = checked((cropY + row) * sourceStride + cropX);
                source.Slice(sourceOffset, cropWidth).CopyTo(yPlane.Slice(row * cropWidth, cropWidth));
            }

            var chromaStart = sourceStride * sourceHeight;
            var sourceChromaX = cropX / 2;
            var sourceChromaY = cropY / 2;
            for (var row = 0; row < chromaHeight; row++)
            {
                var sourceRow = source.Slice(
                    chromaStart + (sourceChromaY + row) * sourceStride + sourceChromaX * 2,
                    chromaWidth * 2);
                var destinationOffset = row * chromaWidth;
                for (var column = 0; column < chromaWidth; column++)
                {
                    vPlane[destinationOffset + column] = sourceRow[column * 2];
                    uPlane[destinationOffset + column] = sourceRow[column * 2 + 1];
                }
            }

            var frame = new Yuv420Frame(
                sequence,
                capturedAt,
                cropWidth,
                cropHeight,
                rotationDegrees,
                yOwner,
                yLength,
                cropWidth,
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
