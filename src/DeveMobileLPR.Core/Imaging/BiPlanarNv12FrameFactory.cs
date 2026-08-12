using System.Buffers;

namespace DeveMobileLPR.Imaging;

/// <summary>
/// Copies an NV12 image whose luma and interleaved chroma planes have independent strides.
/// This is the native layout exposed by iOS <c>CVPixelBuffer</c> camera and video frames.
/// </summary>
public static class BiPlanarNv12FrameFactory
{
    public static Yuv420Frame Create(
        ReadOnlySpan<byte> luma,
        int lumaStride,
        ReadOnlySpan<byte> chroma,
        int chromaStride,
        int width,
        int height,
        long sequence,
        DateTimeOffset capturedAt,
        int rotationDegrees = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (lumaStride < width) throw new ArgumentOutOfRangeException(nameof(lumaStride));

        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        if (chromaStride < checked(chromaWidth * 2)) throw new ArgumentOutOfRangeException(nameof(chromaStride));
        if (luma.Length < checked(lumaStride * height)) throw new ArgumentException("The luma plane is incomplete.", nameof(luma));
        if (chroma.Length < checked(chromaStride * chromaHeight)) throw new ArgumentException("The chroma plane is incomplete.", nameof(chroma));

        var yLength = checked(width * height);
        var chromaLength = checked(chromaWidth * chromaHeight);
        var yOwner = MemoryPool<byte>.Shared.Rent(yLength);
        var uOwner = MemoryPool<byte>.Shared.Rent(chromaLength);
        var vOwner = MemoryPool<byte>.Shared.Rent(chromaLength);
        try
        {
            var y = yOwner.Memory.Span[..yLength];
            var u = uOwner.Memory.Span[..chromaLength];
            var v = vOwner.Memory.Span[..chromaLength];
            for (var row = 0; row < height; row++)
            {
                luma.Slice(row * lumaStride, width).CopyTo(y.Slice(row * width, width));
            }

            for (var row = 0; row < chromaHeight; row++)
            {
                var source = chroma.Slice(row * chromaStride, chromaWidth * 2);
                var destination = row * chromaWidth;
                for (var column = 0; column < chromaWidth; column++)
                {
                    u[destination + column] = source[column * 2];
                    v[destination + column] = source[column * 2 + 1];
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
}
