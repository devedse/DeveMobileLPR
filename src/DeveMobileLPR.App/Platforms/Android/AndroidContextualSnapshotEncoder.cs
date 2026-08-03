using System.Buffers;
using Android.Graphics;
using DeveMobileLPR.Application;

namespace DeveMobileLPR.App;

internal sealed class AndroidContextualSnapshotEncoder : IContextualSnapshotEncoder
{
    public Task EncodeJpegAsync(
        ReadOnlyMemory<byte> rgbPixels,
        int width,
        int height,
        string destinationPath,
        CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var colorCount = checked(width * height);
        var colors = ArrayPool<int>.Shared.Rent(colorCount);
        try
        {
            var source = rgbPixels.Span;
            for (var pixel = 0; pixel < colorCount; pixel++)
            {
                var sourceOffset = pixel * 3;
                colors[pixel] = unchecked((int)0xFF000000)
                    | source[sourceOffset] << 16
                    | source[sourceOffset + 1] << 8
                    | source[sourceOffset + 2];
            }

            using var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)
                ?? throw new InvalidOperationException("Android could not allocate the contextual snapshot bitmap.");
            bitmap.SetPixels(colors, 0, width, 0, 0, width, height);
            await using var stream = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous);
            cancellationToken.ThrowIfCancellationRequested();
            if (!bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 85, stream))
            {
                throw new InvalidDataException("Android could not encode the contextual snapshot.");
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(colors, clearArray: true);
        }
    }, cancellationToken);
}