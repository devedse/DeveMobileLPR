using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace DeveMobileLPR.Application;

public sealed class ImageSharpContextualSnapshotEncoder : IContextualSnapshotEncoder
{
    private const int JpegQuality = 85;

    public async Task EncodeJpegAsync(
        ReadOnlyMemory<byte> rgbPixels,
        int width,
        int height,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var expectedLength = checked(width * height * 3);
        if (rgbPixels.Length != expectedLength)
        {
            throw new ArgumentException(
                $"Expected {expectedLength} packed RGB bytes, but received {rgbPixels.Length}.",
                nameof(rgbPixels));
        }

        using var image = Image.LoadPixelData<Rgb24>(rgbPixels.Span, width, height);
        await image.SaveAsJpegAsync(
            destinationPath,
            new JpegEncoder { Quality = JpegQuality },
            cancellationToken).ConfigureAwait(false);
    }
}