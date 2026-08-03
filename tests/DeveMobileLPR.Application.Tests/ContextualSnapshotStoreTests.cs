using System.Buffers;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Application.Tests;

public sealed class ContextualSnapshotStoreTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        $"DeveMobileLPR-snapshot-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveRedactsPlateAndKeepsOnlyRelativeReference()
    {
        var encoder = new RecordingEncoder();
        var store = new ContextualSnapshotStore(_rootDirectory, encoder);
        using var frame = CreateWhiteFrame(6, 4);

        var reference = await store.SaveAsync(
            42,
            frame,
            new BoundingBox(2, 1, 4, 3),
            CancellationToken.None);

        Assert.Equal("vehicle-snapshots/42.jpg", reference);
        Assert.Equal((6, 4), (encoder.Width, encoder.Height));
        Assert.Equal([255, 255, 255], PixelAt(encoder.Pixels, encoder.Width, 0, 0));
        Assert.Equal([245, 197, 66], PixelAt(encoder.Pixels, encoder.Width, 2, 2));
        Assert.Equal([0, 0, 0], PixelAt(encoder.Pixels, encoder.Width, 4, 2));
        Assert.NotNull(store.ResolvePath(reference));
        Assert.Null(store.ResolvePath("../42.jpg"));
        Assert.Null(store.ResolvePath("vehicle-snapshots/not-a-sighting.jpg"));

        await store.DeleteAllAsync(CancellationToken.None);

        Assert.Null(store.ResolvePath(reference));
    }

    [Fact]
    public async Task SaveCropsToEstimatedVehicleRegionAroundPlate()
    {
        var encoder = new RecordingEncoder();
        var store = new ContextualSnapshotStore(_rootDirectory, encoder);
        using var frame = CreateWhiteFrame(100, 80);

        await store.SaveAsync(
            43,
            frame,
            new BoundingBox(50, 50, 60, 55),
            CancellationToken.None);

        Assert.Equal((50, 40), (encoder.Width, encoder.Height));
        Assert.Equal([255, 255, 255], PixelAt(encoder.Pixels, encoder.Width, 0, 0));
        Assert.Equal([245, 197, 66], PixelAt(encoder.Pixels, encoder.Width, 20, 20));
        Assert.Equal([0, 0, 0], PixelAt(encoder.Pixels, encoder.Width, 25, 22));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private static byte[] PixelAt(byte[] pixels, int width, int x, int y)
    {
        var offset = (y * width + x) * 3;
        return pixels[offset..(offset + 3)];
    }

    private static Yuv420Frame CreateWhiteFrame(int width, int height)
    {
        var y = new ArrayMemoryOwner(width * height, 235);
        var chromaLength = (width / 2) * (height / 2);
        return new Yuv420Frame(
            1,
            DateTimeOffset.UtcNow,
            width,
            height,
            0,
            y,
            width * height,
            width,
            1,
            new ArrayMemoryOwner(chromaLength, 128),
            chromaLength,
            width / 2,
            1,
            new ArrayMemoryOwner(chromaLength, 128),
            chromaLength,
            width / 2,
            1);
    }

    private sealed class RecordingEncoder : IContextualSnapshotEncoder
    {
        public byte[] Pixels { get; private set; } = [];
        public int Width { get; private set; }
        public int Height { get; private set; }

        public async Task EncodeJpegAsync(
            ReadOnlyMemory<byte> rgbPixels,
            int width,
            int height,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            Pixels = rgbPixels.ToArray();
            Width = width;
            Height = height;
            await File.WriteAllBytesAsync(destinationPath, [1], cancellationToken);
        }
    }

    private sealed class ArrayMemoryOwner : IMemoryOwner<byte>
    {
        private byte[]? _bytes;

        public ArrayMemoryOwner(int length, byte value)
        {
            _bytes = new byte[length];
            Array.Fill(_bytes, value);
        }

        public Memory<byte> Memory => _bytes ?? throw new ObjectDisposedException(nameof(ArrayMemoryOwner));
        public void Dispose() => _bytes = null;
    }
}