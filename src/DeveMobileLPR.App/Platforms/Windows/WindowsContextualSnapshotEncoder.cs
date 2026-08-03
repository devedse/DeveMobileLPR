using System.Buffers;
using DeveMobileLPR.Application;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeveMobileLPR.App;

internal sealed class WindowsContextualSnapshotEncoder : IContextualSnapshotEncoder
{
    public async Task EncodeJpegAsync(
        ReadOnlyMemory<byte> rgbPixels,
        int width,
        int height,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var pixelCount = checked(width * height);
        var rgbaPixels = ArrayPool<byte>.Shared.Rent(checked(pixelCount * 4));
        try
        {
            var source = rgbPixels.Span;
            for (var pixel = 0; pixel < pixelCount; pixel++)
            {
                var sourceOffset = pixel * 3;
                var destinationOffset = pixel * 4;
                rgbaPixels[destinationOffset] = source[sourceOffset];
                rgbaPixels[destinationOffset + 1] = source[sourceOffset + 1];
                rgbaPixels[destinationOffset + 2] = source[sourceOffset + 2];
                rgbaPixels[destinationOffset + 3] = byte.MaxValue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(destinationPath)
                ?? throw new ArgumentException("A destination directory is required.", nameof(destinationPath));
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            var file = await folder.CreateFileAsync(
                Path.GetFileName(destinationPath),
                CreationCollisionOption.ReplaceExisting);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var properties = new BitmapPropertySet
            {
                ["ImageQuality"] = new BitmapTypedValue(0.85f, PropertyType.Single)
            };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, properties);
            encoder.SetPixelData(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Ignore,
                (uint)width,
                (uint)height,
                96,
                96,
                rgbaPixels.AsSpan(0, pixelCount * 4).ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            await encoder.FlushAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rgbaPixels, clearArray: true);
        }
    }
}