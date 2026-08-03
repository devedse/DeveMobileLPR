using System.Buffers;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Application;

public sealed class ContextualSnapshotStore : IContextualSnapshotStore
{
    private const string DirectoryName = "vehicle-snapshots";
    private const int MaximumDimension = 1280;
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

        var scale = Math.Min(1d, (double)MaximumDimension / Math.Max(frame.OrientedWidth, frame.OrientedHeight));
        var width = Math.Max(1, (int)Math.Round(frame.OrientedWidth * scale));
        var height = Math.Max(1, (int)Math.Round(frame.OrientedHeight * scale));
        var pixelLength = checked(width * height * 3);
        var pixels = ArrayPool<byte>.Shared.Rent(pixelLength);
        var snapshotDirectory = Path.Combine(_rootDirectory, DirectoryName);
        var fileName = $"{sightingId}.jpg";
        var destinationPath = Path.Combine(snapshotDirectory, fileName);
        var temporaryPath = Path.Combine(snapshotDirectory, $".{fileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            FillRedactedRgb(frame, plateBounds, pixels.AsSpan(0, pixelLength), width, height);
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

    private static void FillRedactedRgb(
        Yuv420Frame frame,
        BoundingBox plateBounds,
        Span<byte> destination,
        int width,
        int height)
    {
        var redactionBounds = plateBounds.Expand(0.15f, 0.35f, frame.OrientedWidth, frame.OrientedHeight);
        var scaleX = (double)frame.OrientedWidth / width;
        var scaleY = (double)frame.OrientedHeight / height;
        var offset = 0;

        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(frame.OrientedHeight - 1, (int)((y + 0.5d) * scaleY));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(frame.OrientedWidth - 1, (int)((x + 0.5d) * scaleX));
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
    }
}