using SixLabors.ImageSharp;

namespace DeveMobileLPR.Application.Tests;

public sealed class ImageSharpVehicleImageEncoderTests : IDisposable
{
    private readonly string _destinationPath = Path.Combine(
        Path.GetTempPath(),
        $"DeveMobileLPR-encoder-test-{Guid.NewGuid():N}.jpg");

    [Fact]
    public async Task EncodeJpegWritesDecodableImageWithExpectedDimensions()
    {
        var encoder = new ImageSharpVehicleImageEncoder();

        await encoder.EncodeJpegAsync(
            new byte[]
            {
                255, 0, 0, 0, 255, 0,
                0, 0, 255, 255, 255, 255
            },
            2,
            2,
            _destinationPath,
            CancellationToken.None);

        using var image = await Image.LoadAsync(_destinationPath);

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
    }

    [Fact]
    public async Task EncodeJpegRejectsUnexpectedPixelLength()
    {
        var encoder = new ImageSharpVehicleImageEncoder();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => encoder.EncodeJpegAsync(
            new byte[] { 255, 0, 0 },
            2,
            2,
            _destinationPath,
            CancellationToken.None));

        Assert.Equal("rgbPixels", exception.ParamName);
    }

    public void Dispose() => File.Delete(_destinationPath);
}