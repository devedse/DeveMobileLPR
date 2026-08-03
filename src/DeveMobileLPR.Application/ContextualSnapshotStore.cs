using System.Buffers;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Application;

public sealed class ContextualSnapshotStore : IContextualSnapshotStore
{
    private const string DirectoryName = "vehicle-snapshots";
    private const int MaximumDimension = 1280;
    private const float HorizontalPlateMargin = 2f;
    private const float TopPlateMargin = 5f;
    private const float BottomPlateMargin = 3f;
    private const byte DetectionBorderRed = 245;
    private const byte DetectionBorderGreen = 197;
    private const byte DetectionBorderBlue = 66;
    private readonly string _rootDirectory;
    private readonly IContextualSnapshotEncoder _encoder;

    public ContextualSnapshotStore(string rootDirectory, IContextualSnapshotEncoder encoder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
    }

    public async Task<string> SaveAsync(
        long sightingId,
        Yuv420Frame frame,
        BoundingBox plateBounds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sightingId);
        ArgumentNullException.ThrowIfNull(frame);
        if (plateBounds.IsEmpty)
        {
            throw new ArgumentException("Plate bounds are required for redaction.", nameof(plateBounds));
        }

        var clampedPlateBounds = plateBounds.Clamp(frame.OrientedWidth, frame.OrientedHeight);
        if (clampedPlateBounds.IsEmpty)
        {
            throw new ArgumentException("Plate bounds must intersect the frame.", nameof(plateBounds));
        }

        var crop = CreateVehicleCrop(clampedPlateBounds, frame.OrientedWidth, frame.OrientedHeight);
        var scale = Math.Min(1d, (double)MaximumDimension / Math.Max(crop.Width, crop.Height));
        var width = Math.Max(1, (int)Math.Round(crop.Width * scale));
        var height = Math.Max(1, (int)Math.Round(crop.Height * scale));
        var pixelLength = checked(width * height * 3);
        var pixels = ArrayPool<byte>.Shared.Rent(pixelLength);
        var snapshotDirectory = Path.Combine(_rootDirectory, DirectoryName);
        var fileName = $"{sightingId}.jpg";
        var destinationPath = Path.Combine(snapshotDirectory, fileName);
        var temporaryPath = Path.Combine(snapshotDirectory, $".{fileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            FillSnapshotRgb(frame, clampedPlateBounds, crop, pixels.AsSpan(0, pixelLength), width, height);
            Directory.CreateDirectory(snapshotDirectory);
            await _encoder.EncodeJpegAsync(
                pixels.AsMemory(0, pixelLength),
                width,
                height,
                temporaryPath,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, true);
            return $"{DirectoryName}/{fileName}";
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels, clearArray: true);
            File.Delete(temporaryPath);
        }
    }

    public string? ResolvePath(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var normalized = reference.Replace('\\', '/');
        if (!normalized.StartsWith($"{DirectoryName}/", StringComparison.Ordinal)
            || normalized.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var fileName = normalized[(DirectoryName.Length + 1)..];
        if (fileName.Contains('/', StringComparison.Ordinal)
            || !fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(Path.GetFileNameWithoutExtension(fileName), out var sightingId)
            || sightingId <= 0)
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(_rootDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var snapshotDirectory = Path.GetFullPath(Path.Combine(_rootDirectory, DirectoryName)) + Path.DirectorySeparatorChar;
        return path.StartsWith(snapshotDirectory, StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            ? path
            : null;
    }

    public Task DeleteAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshotDirectory = Path.Combine(_rootDirectory, DirectoryName);
        if (Directory.Exists(snapshotDirectory))
        {
            Directory.Delete(snapshotDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static void FillSnapshotRgb(
        Yuv420Frame frame,
        BoundingBox plateBounds,
        VehicleCrop crop,
        Span<byte> destination,
        int width,
        int height)
    {
        var redactionBounds = plateBounds.Expand(0.15f, 0.35f, frame.OrientedWidth, frame.OrientedHeight);
        var scaleX = (double)crop.Width / width;
        var scaleY = (double)crop.Height / height;
        var offset = 0;

        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(crop.Bottom - 1, crop.Top + (int)((y + 0.5d) * scaleY));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(crop.Right - 1, crop.Left + (int)((x + 0.5d) * scaleX));
                if (sourceX >= redactionBounds.Left && sourceX < redactionBounds.Right
                    && sourceY >= redactionBounds.Top && sourceY < redactionBounds.Bottom)
                {
                    destination[offset++] = 0;
                    destination[offset++] = 0;
                    destination[offset++] = 0;
                    continue;
                }

                frame.GetRgb(sourceX, sourceY, out var red, out var green, out var blue);
                destination[offset++] = red;
                destination[offset++] = green;
                destination[offset++] = blue;
            }
        }

        DrawDetectionBorder(plateBounds, crop, destination, width, height, scaleX, scaleY);
    }

    private static void DrawDetectionBorder(
        BoundingBox plateBounds,
        VehicleCrop crop,
        Span<byte> pixels,
        int width,
        int height,
        double scaleX,
        double scaleY)
    {
        var left = Math.Clamp((int)Math.Floor((plateBounds.Left - crop.Left) / scaleX), 0, width - 1);
        var top = Math.Clamp((int)Math.Floor((plateBounds.Top - crop.Top) / scaleY), 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling((plateBounds.Right - crop.Left) / scaleX) - 1, left, width - 1);
        var bottom = Math.Clamp((int)Math.Ceiling((plateBounds.Bottom - crop.Top) / scaleY) - 1, top, height - 1);
        var thickness = Math.Clamp(Math.Min(width, height) / 120, 1, 4);

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                if (x >= left + thickness && x <= right - thickness
                    && y >= top + thickness && y <= bottom - thickness)
                {
                    continue;
                }

                var offset = (y * width + x) * 3;
                pixels[offset] = DetectionBorderRed;
                pixels[offset + 1] = DetectionBorderGreen;
                pixels[offset + 2] = DetectionBorderBlue;
            }
        }
    }

    private static VehicleCrop CreateVehicleCrop(BoundingBox plateBounds, int frameWidth, int frameHeight) => new(
        Math.Max(0, (int)Math.Floor(plateBounds.Left - plateBounds.Width * HorizontalPlateMargin)),
        Math.Max(0, (int)Math.Floor(plateBounds.Top - plateBounds.Height * TopPlateMargin)),
        Math.Min(frameWidth, (int)Math.Ceiling(plateBounds.Right + plateBounds.Width * HorizontalPlateMargin)),
        Math.Min(frameHeight, (int)Math.Ceiling(plateBounds.Bottom + plateBounds.Height * BottomPlateMargin)));

    private readonly record struct VehicleCrop(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
}