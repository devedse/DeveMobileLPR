using System.IO.Compression;
using System.Text.Json;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.Tests;

public sealed class HistoryBackupServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"DeveMobileLPR-backup-test-{Guid.NewGuid():N}");
    private string _databasePath = null!;
    private SightingRepository _repository = null!;
    private HistoryBackupService _backup = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "sightings.sqlite");
        _repository = new SightingRepository(_databasePath);
        _backup = new HistoryBackupService(_root, _databasePath);
        await _repository.InitializeAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Backup_RoundTripsHistoryAndScreenshots_WithoutRdw_AndIgnoresFormatVersion()
    {
        var createdAt = new DateTimeOffset(2026, 8, 20, 14, 15, 16, TimeSpan.FromHours(2));
        var trip = await _repository.StartTripAsync(createdAt, new GeoPoint(52.09, 5.12, 3), CancellationToken.None);
        await _repository.AddTripPointAsync(trip.Id, createdAt, new GeoPoint(52.09, 5.12, 3), CancellationToken.None);
        var sighting = await _repository.AddOrMergeAsync(
            Confirmed("AB1234", createdAt.AddSeconds(10)),
            new GeoPoint(52.091, 5.121, 4),
            new VehicleRecord("AB1234", "Audi", "A6", 85_000m, 2024, "Benzine", "Personenauto"),
            trip.Id,
            CancellationToken.None);
        var snapshotDirectory = Path.Combine(_root, HistoryBackupService.SnapshotDirectoryName);
        Directory.CreateDirectory(snapshotDirectory);
        var snapshotBytes = new byte[] { 0xff, 0xd8, 0xff, 0xdb, 1, 2, 3, 0xff, 0xd9 };
        var snapshotPath = Path.Combine(snapshotDirectory, $"{sighting.Id}.jpg");
        await File.WriteAllBytesAsync(snapshotPath, snapshotBytes);
        await _repository.SetSnapshotReferenceAsync(
            sighting.Id,
            $"{HistoryBackupService.SnapshotDirectoryName}/{sighting.Id}.jpg",
            CancellationToken.None);
        await _repository.EndTripAsync(trip.Id, createdAt.AddMinutes(1), null, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_root, "rdw.sqlite"), "must not be exported");

        var archivePath = await _backup.CreateAsync(_root, "0.1.0", "42", createdAt, CancellationToken.None);

        Assert.Equal("devemobilelpr-backup-20260820-141516-v0.1.0-b42.zip", Path.GetFileName(archivePath));
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            var names = archive.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Contains(HistoryBackupService.ManifestEntryName, names);
            Assert.Contains(HistoryBackupService.DatabaseEntryName, names);
            Assert.Contains($"{HistoryBackupService.SnapshotDirectoryName}/{sighting.Id}.jpg", names);
            Assert.DoesNotContain(names, name => name.Contains("rdw", StringComparison.OrdinalIgnoreCase));
        }

        await ChangeFormatVersionAsync(archivePath, 999);
        await _repository.DeleteHistoryAsync(CancellationToken.None);
        await File.WriteAllBytesAsync(snapshotPath, [9, 9, 9]);
        await using (var stream = File.OpenRead(archivePath))
        {
            var restored = await _backup.RestoreAsync(stream, CancellationToken.None);
            Assert.Equal(999, restored.Manifest.BackupFormatVersion);
            Assert.Equal("0.1.0", restored.Manifest.AppVersion);
            Assert.Equal("42", restored.Manifest.AppBuild);
            Assert.Equal(createdAt.ToUniversalTime(), restored.Manifest.CreatedAtUtc);
        }

        var restoredSighting = Assert.Single(await _repository.GetAllSightingsAsync(CancellationToken.None));
        Assert.Equal("AB1234", restoredSighting.NormalizedPlate);
        Assert.Equal(trip.Id, restoredSighting.TripId);
        Assert.Equal(snapshotBytes, await File.ReadAllBytesAsync(snapshotPath));
        Assert.Single(await _repository.GetTripsAsync(0, 10, CancellationToken.None));
        Assert.Equal("must not be exported", await File.ReadAllTextAsync(Path.Combine(_root, "rdw.sqlite")));
    }

    [Fact]
    public void BackupSizeValidation_MatchesRestoreLimits()
    {
        Assert.Throws<InvalidDataException>(() => HistoryBackupService.ValidateBackupSize(
            [("oversized.jpg", 512L * 1024 * 1024 + 1)]));

        Assert.Throws<InvalidDataException>(() => HistoryBackupService.ValidateBackupSize(
            Enumerable.Range(0, 5).Select(index =>
                ($"snapshots/{index}.jpg", 500L * 1024 * 1024))));

        HistoryBackupService.ValidateBackupSize(
            [("history/sightings.sqlite", 128L * 1024 * 1024), ("manifest.json", 1024L)]);
    }

    [Fact]
    public async Task ArchiveCreation_RemovesPartialFileWhenCancelled()
    {
        var sourceDirectory = Path.Combine(_root, "archive-source");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "manifest.json"), "{}");
        var destinationPath = Path.Combine(_root, "cancelled.partial.zip");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HistoryBackupService.CreateArchiveAsync(
                sourceDirectory,
                destinationPath,
                DateTimeOffset.UtcNow,
                cancellation.Token));

        Assert.False(File.Exists(destinationPath));
    }

    private static async Task ChangeFormatVersionAsync(string archivePath, int version)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var entry = archive.GetEntry(HistoryBackupService.ManifestEntryName)!;
        HistoryBackupManifest manifest;
        await using (var input = entry.Open())
        {
            manifest = (await JsonSerializer.DeserializeAsync<HistoryBackupManifest>(
                input,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        }
        entry.Delete();
        var replacement = archive.CreateEntry(HistoryBackupService.ManifestEntryName);
        await using var output = replacement.Open();
        await JsonSerializer.SerializeAsync(
            output,
            manifest with { BackupFormatVersion = version },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static ConfirmedPlate Confirmed(string plate, DateTimeOffset time) => new(
        Guid.NewGuid(),
        time.AddSeconds(-1),
        time,
        new BoundingBox(10, 10, 100, 40),
        new ConsensusResult(plate, PlateText.FormatDutchPlate(plate), "Netherlands", 0.95f, 3));
}
