using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DeveMobileLPR.Storage;

public sealed record HistoryBackupFile(string Path, long Length, string Sha256);

public sealed record HistoryBackupManifest(
    int BackupFormatVersion,
    DateTimeOffset CreatedAtUtc,
    string AppVersion,
    string AppBuild,
    int TripCount,
    int SightingCount,
    int TripPointCount,
    int VehicleSnapshotCount,
    IReadOnlyList<HistoryBackupFile> Files);

public sealed record HistoryBackupRestoreResult(HistoryBackupManifest Manifest);

/// <summary>
/// Creates and restores portable history archives. RDW data is deliberately excluded: it is a
/// replaceable reference dataset, while trips, sightings, route points, and vehicle snapshots are
/// user-created data that cannot be reconstructed.
/// </summary>
public sealed class HistoryBackupService
{
    public const int CurrentBackupFormatVersion = 1;
    public const string ManifestEntryName = "manifest.json";
    public const string DatabaseEntryName = "history/sightings.sqlite";
    public const string SnapshotDirectoryName = "vehicle-snapshots";
    private const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumManifestBytes = 1024 * 1024;
    private const long MaximumEntryBytes = 512L * 1024 * 1024;
    private const int MaximumEntryCount = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _rootDirectory;
    private readonly string _databasePath;
    private readonly string _snapshotDirectory;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public HistoryBackupService(string rootDirectory, string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _databasePath = Path.GetFullPath(databasePath);
        _snapshotDirectory = Path.Combine(_rootDirectory, SnapshotDirectoryName);
        if (!IsWithin(_rootDirectory, _databasePath))
        {
            throw new ArgumentException("The history database must be inside the application data directory.", nameof(databasePath));
        }
    }

    public async Task<string> CreateAsync(
        string destinationDirectory,
        string appVersion,
        string appBuild,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(appBuild);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stagingDirectory = Path.Combine(
            Path.GetFullPath(destinationDirectory),
            $".devemobilelpr-backup-{Guid.NewGuid():N}");
        string? incompleteArchivePath = null;
        try
        {
            if (!File.Exists(_databasePath))
            {
                throw new FileNotFoundException("The history database has not been created yet.", _databasePath);
            }

            Directory.CreateDirectory(destinationDirectory);
            Directory.CreateDirectory(Path.Combine(stagingDirectory, "history"));
            var stagedDatabase = Path.Combine(stagingDirectory, DatabaseEntryName.Replace('/', Path.DirectorySeparatorChar));
            await CreateDatabaseSnapshotAsync(_databasePath, stagedDatabase, cancellationToken).ConfigureAwait(false);

            var stagedSnapshots = Path.Combine(stagingDirectory, SnapshotDirectoryName);
            if (Directory.Exists(_snapshotDirectory))
            {
                Directory.CreateDirectory(stagedSnapshots);
                foreach (var source in Directory.EnumerateFiles(_snapshotDirectory, "*.jpg", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Copy(source, Path.Combine(stagedSnapshots, Path.GetFileName(source)), overwrite: false);
                }
            }

            await ValidateDatabaseAsync(stagedDatabase, cancellationToken).ConfigureAwait(false);
            await ValidateSnapshotReferencesAsync(stagedDatabase, stagedSnapshots, cancellationToken).ConfigureAwait(false);
            var counts = await ReadCountsAsync(stagedDatabase, cancellationToken).ConfigureAwait(false);
            DeleteDatabaseSidecars(stagedDatabase);
            var files = await CreateFileManifestAsync(stagingDirectory, cancellationToken).ConfigureAwait(false);
            var manifest = new HistoryBackupManifest(
                CurrentBackupFormatVersion,
                createdAt.ToUniversalTime(),
                appVersion,
                appBuild,
                counts.Trips,
                counts.Sightings,
                counts.TripPoints,
                files.Count(file => file.Path.StartsWith($"{SnapshotDirectoryName}/", StringComparison.Ordinal)),
                files);
            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, ManifestEntryName),
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            ValidateStagedBackupSize(stagingDirectory);

            var safeVersion = FileNamePart(appVersion);
            var safeBuild = FileNamePart(appBuild);
            var fileName = $"devemobilelpr-backup-{createdAt:yyyyMMdd-HHmmss}-v{safeVersion}-b{safeBuild}.zip";
            var destinationPath = UniquePath(Path.Combine(Path.GetFullPath(destinationDirectory), fileName));
            incompleteArchivePath = $"{destinationPath}.partial-{Guid.NewGuid():N}";
            await CreateArchiveAsync(stagingDirectory, incompleteArchivePath, createdAt, cancellationToken).ConfigureAwait(false);
            if (new FileInfo(incompleteArchivePath).Length > MaximumArchiveBytes)
            {
                throw new InvalidDataException("The compressed backup is too large to restore.");
            }
            File.Move(incompleteArchivePath, destinationPath);
            incompleteArchivePath = null;
            return destinationPath;
        }
        finally
        {
            if (incompleteArchivePath is not null)
            {
                File.Delete(incompleteArchivePath);
            }
            DeleteDirectory(stagingDirectory);
            _operationGate.Release();
        }
    }

    public async Task<HistoryBackupManifest> InspectAsync(
        Stream archive,
        CancellationToken cancellationToken)
    {
        var stagedArchive = await StageArchiveAsync(archive, cancellationToken).ConfigureAwait(false);
        try
        {
            using var zip = ZipFile.OpenRead(stagedArchive);
            ValidateEntryList(zip);
            var manifest = await ReadManifestAsync(zip, cancellationToken).ConfigureAwait(false);
            ValidateManifestMetadata(manifest);
            return manifest;
        }
        finally
        {
            File.Delete(stagedArchive);
        }
    }

    public async Task<HistoryBackupRestoreResult> RestoreAsync(
        Stream archive,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stagedArchive = string.Empty;
        var extractionDirectory = Path.Combine(
            Path.GetTempPath(),
            $"devemobilelpr-restore-{Guid.NewGuid():N}");
        var installDirectory = Path.Combine(_rootDirectory, $".history-restore-{Guid.NewGuid():N}");
        try
        {
            stagedArchive = await StageArchiveAsync(archive, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(extractionDirectory);
            HistoryBackupManifest manifest;
            using (var zip = ZipFile.OpenRead(stagedArchive))
            {
                ValidateEntryList(zip);
                manifest = await ReadManifestAsync(zip, cancellationToken).ConfigureAwait(false);
                await ExtractAsync(zip, extractionDirectory, cancellationToken).ConfigureAwait(false);
            }

            var extractedDatabase = Path.Combine(
                extractionDirectory,
                DatabaseEntryName.Replace('/', Path.DirectorySeparatorChar));
            var extractedSnapshots = Path.Combine(extractionDirectory, SnapshotDirectoryName);
            await ValidateDatabaseAsync(extractedDatabase, cancellationToken).ConfigureAwait(false);
            await ValidateSnapshotReferencesAsync(extractedDatabase, extractedSnapshots, cancellationToken).ConfigureAwait(false);
            var counts = await ReadCountsAsync(extractedDatabase, cancellationToken).ConfigureAwait(false);
            DeleteDatabaseSidecars(extractedDatabase);
            await ValidateManifestAsync(extractionDirectory, manifest, cancellationToken).ConfigureAwait(false);
            if (counts.Trips != manifest.TripCount
                || counts.Sightings != manifest.SightingCount
                || counts.TripPoints != manifest.TripPointCount)
            {
                throw new InvalidDataException("The backup database counts do not match its manifest.");
            }

            await InstallAsync(extractedDatabase, extractedSnapshots, installDirectory, cancellationToken)
                .ConfigureAwait(false);
            return new HistoryBackupRestoreResult(manifest);
        }
        finally
        {
            if (!string.IsNullOrEmpty(stagedArchive))
            {
                File.Delete(stagedArchive);
            }
            DeleteDirectory(extractionDirectory);
            DeleteDirectory(installDirectory);
            _operationGate.Release();
        }
    }

    private async Task InstallAsync(
        string sourceDatabase,
        string sourceSnapshots,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(installDirectory);
        var incomingDatabase = Path.Combine(installDirectory, "incoming.sqlite");
        var rollbackDatabase = Path.Combine(installDirectory, "rollback.sqlite");
        var incomingSnapshots = Path.Combine(installDirectory, "incoming-snapshots");
        var rollbackSnapshots = Path.Combine(installDirectory, "rollback-snapshots");
        File.Copy(sourceDatabase, incomingDatabase, overwrite: false);
        CopyDirectory(sourceSnapshots, incomingSnapshots, cancellationToken);

        var hadDatabase = File.Exists(_databasePath);
        var hadSnapshots = Directory.Exists(_snapshotDirectory);
        SqliteConnection.ClearAllPools();
        if (hadDatabase)
        {
            await CreateDatabaseSnapshotAsync(_databasePath, rollbackDatabase, cancellationToken).ConfigureAwait(false);
        }
        if (hadSnapshots)
        {
            Directory.Move(_snapshotDirectory, rollbackSnapshots);
        }

        try
        {
            DeleteDatabaseFiles(_databasePath);
            File.Move(incomingDatabase, _databasePath);
            if (Directory.Exists(incomingSnapshots))
            {
                Directory.Move(incomingSnapshots, _snapshotDirectory);
            }
            await ValidateDatabaseAsync(_databasePath, cancellationToken).ConfigureAwait(false);
            SqliteConnection.ClearAllPools();
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(_databasePath);
            DeleteDirectory(_snapshotDirectory);
            if (hadDatabase && File.Exists(rollbackDatabase))
            {
                File.Move(rollbackDatabase, _databasePath);
            }
            if (hadSnapshots && Directory.Exists(rollbackSnapshots))
            {
                Directory.Move(rollbackSnapshots, _snapshotDirectory);
            }
            throw;
        }
    }

    private static async Task<string> StageArchiveAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var path = Path.Combine(Path.GetTempPath(), $"devemobilelpr-backup-{Guid.NewGuid():N}.zip");
        try
        {
            await using var destination = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                total += read;
                if (total > MaximumArchiveBytes)
                {
                    throw new InvalidDataException("The backup archive is too large.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private static void ValidateEntryList(ZipArchive zip)
    {
        if (zip.Entries.Count is 0 or > MaximumEntryCount)
        {
            throw new InvalidDataException("The backup contains an invalid number of files.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        long totalLength = 0;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                NormalizeDirectoryEntryName(entry.FullName);
                continue;
            }
            var name = NormalizeEntryName(entry.FullName);
            if (!names.Add(name))
            {
                throw new InvalidDataException($"The backup contains duplicate entry '{name}'.");
            }
            if (!IsAllowedEntry(name))
            {
                throw new InvalidDataException($"The backup contains unexpected entry '{name}'.");
            }
            if (entry.Length < 0 || entry.Length > MaximumEntryBytes)
            {
                throw new InvalidDataException($"Backup entry '{name}' is too large.");
            }
            totalLength = checked(totalLength + entry.Length);
            if (totalLength > MaximumArchiveBytes)
            {
                throw new InvalidDataException("The extracted backup is too large.");
            }
        }

        if (!names.Contains(ManifestEntryName) || !names.Contains(DatabaseEntryName))
        {
            throw new InvalidDataException("The backup is missing its manifest or history database.");
        }
    }

    private static bool IsAllowedEntry(string name) =>
        name is ManifestEntryName or DatabaseEntryName
        || name.StartsWith($"{SnapshotDirectoryName}/", StringComparison.Ordinal)
            && name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            && !name[(SnapshotDirectoryName.Length + 1)..].Contains('/');

    private static string NormalizeEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('\\')
            || name.StartsWith('/')
            || name.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"Backup entry '{name}' has an unsafe path.");
        }
        return name;
    }

    private static void NormalizeDirectoryEntryName(string name)
    {
        var trimmed = name.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed)
            || name.Contains('\\')
            || name.StartsWith('/')
            || trimmed.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"Backup entry '{name}' has an unsafe path.");
        }
    }

    private static async Task<HistoryBackupManifest> ReadManifestAsync(
        ZipArchive zip,
        CancellationToken cancellationToken)
    {
        var entry = zip.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("The backup manifest is missing.");
        if (entry.Length is <= 0 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("The backup manifest has an invalid size.");
        }
        await using var stream = entry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<HistoryBackupManifest>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        return manifest ?? throw new InvalidDataException("The backup manifest is invalid.");
    }

    private static async Task ExtractAsync(
        ZipArchive zip,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = NormalizeEntryName(entry.FullName);
            var destination = Path.GetFullPath(Path.Combine(destinationDirectory, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Backup entry '{name}' escapes the restore directory.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ValidateManifestAsync(
        string extractionDirectory,
        HistoryBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        ValidateManifestMetadata(manifest);

        var manifestNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = NormalizeEntryName(file.Path);
            if (name == ManifestEntryName || !IsAllowedEntry(name))
            {
                throw new InvalidDataException($"The backup manifest references unexpected file '{name}'.");
            }
            if (!manifestNames.Add(name))
            {
                throw new InvalidDataException($"The backup manifest contains duplicate file '{name}'.");
            }
            var path = Path.Combine(extractionDirectory, name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || new FileInfo(path).Length != file.Length)
            {
                throw new InvalidDataException($"Backup file '{name}' is missing or has the wrong size.");
            }
            var hash = await Sha256Async(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Backup file '{name}' failed its integrity check.");
            }
        }

        var extractedNames = Directory.EnumerateFiles(extractionDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(extractionDirectory, path).Replace('\\', '/'))
            .Where(name => name != ManifestEntryName)
            .ToHashSet(StringComparer.Ordinal);
        if (!manifestNames.SetEquals(extractedNames)
            || !manifestNames.Contains(DatabaseEntryName)
            || manifestNames.Count(name => name.StartsWith($"{SnapshotDirectoryName}/", StringComparison.Ordinal))
                != manifest.VehicleSnapshotCount)
        {
            throw new InvalidDataException("The backup file list does not match its manifest.");
        }
    }

    private static void ValidateManifestMetadata(HistoryBackupManifest manifest)
    {
        // BackupFormatVersion is recorded for diagnosis and future migrations, but is deliberately
        // not enforced yet. The actual contents and current database schema are validated below.
        if (string.IsNullOrWhiteSpace(manifest.AppVersion)
            || string.IsNullOrWhiteSpace(manifest.AppBuild)
            || manifest.Files is null
            || manifest.TripCount < 0
            || manifest.SightingCount < 0
            || manifest.TripPointCount < 0
            || manifest.VehicleSnapshotCount < 0)
        {
            throw new InvalidDataException("The backup manifest is incomplete.");
        }
    }

    private static async Task CreateDatabaseSnapshotAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    private static async Task ValidateDatabaseAsync(string databasePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            throw new InvalidDataException("The backup history database is missing.");
        }
        await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The backup history database failed integrity checking: {result}");
            }
        }

        await using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('trips', 'sightings', 'trip_points', '__EFMigrationsHistory');";
        if (Convert.ToInt32(await schema.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 4)
        {
            throw new InvalidDataException("The backup does not contain a recognized history database schema.");
        }
    }

    private static async Task ValidateSnapshotReferencesAsync(
        string databasePath,
        string snapshotDirectory,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_reference FROM sightings WHERE snapshot_reference IS NOT NULL;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var reference = reader.GetString(0).Replace('\\', '/');
            if (!reference.StartsWith($"{SnapshotDirectoryName}/", StringComparison.Ordinal)
                || reference[(SnapshotDirectoryName.Length + 1)..].Contains('/')
                || reference.Contains("..", StringComparison.Ordinal)
                || !reference.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The history database contains unsafe snapshot reference '{reference}'.");
            }
            var path = Path.Combine(snapshotDirectory, reference[(SnapshotDirectoryName.Length + 1)..]);
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"The backup is missing referenced vehicle snapshot '{reference}'.");
            }
        }
    }

    private static async Task<(int Trips, int Sightings, int TripPoints)> ReadCountsAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken).ConfigureAwait(false);
        return (
            await CountAsync(connection, "trips", cancellationToken).ConfigureAwait(false),
            await CountAsync(connection, "sightings", cancellationToken).ConfigureAwait(false),
            await CountAsync(connection, "trip_points", cancellationToken).ConfigureAwait(false));
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<IReadOnlyList<HistoryBackupFile>> CreateFileManifestAsync(
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var files = new List<HistoryBackupFile>();
        foreach (var path in Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(stagingDirectory, path).Replace('\\', '/');
            files.Add(new HistoryBackupFile(
                relative,
                new FileInfo(path).Length,
                await Sha256Async(path, cancellationToken).ConfigureAwait(false)));
        }
        return files;
    }

    private static void ValidateStagedBackupSize(string stagingDirectory)
    {
        ValidateBackupSize(Directory
            .EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories)
            .Select(path => (Path: Path.GetRelativePath(stagingDirectory, path), Length: new FileInfo(path).Length)));
    }

    internal static void ValidateBackupSize(IEnumerable<(string Path, long Length)> files)
    {
        var count = 0;
        long totalLength = 0;
        foreach (var file in files)
        {
            count++;
            if (count > MaximumEntryCount)
            {
                throw new InvalidDataException("The backup contains too many files.");
            }

            if (file.Length < 0 || file.Length > MaximumEntryBytes)
            {
                throw new InvalidDataException(
                    $"Backup file '{file.Path}' is too large.");
            }

            totalLength = checked(totalLength + file.Length);
            if (totalLength > MaximumArchiveBytes)
            {
                throw new InvalidDataException("The backup is too large.");
            }
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static async Task CreateArchiveAsync(
        string sourceDirectory,
        string destinationPath,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var file = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/');
                var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                entry.LastWriteTime = createdAt;
                await using var input = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = entry.Open();
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            File.Delete(destinationPath);
            throw;
        }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source))
        {
            return;
        }
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(path, Path.Combine(destination, Path.GetFileName(path)), overwrite: false);
        }
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        File.Delete(databasePath);
        DeleteDatabaseSidecars(databasePath);
    }

    private static void DeleteDatabaseSidecars(string databasePath)
    {
        File.Delete(databasePath + "-wal");
        File.Delete(databasePath + "-shm");
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string FileNamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var normalized = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static string UniquePath(string requested)
    {
        if (!File.Exists(requested))
        {
            return requested;
        }
        var directory = Path.GetDirectoryName(requested)!;
        var name = Path.GetFileNameWithoutExtension(requested);
        var extension = Path.GetExtension(requested);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name}-{suffix}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
