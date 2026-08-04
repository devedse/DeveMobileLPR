using System.Diagnostics;
using DeveMobileLPR.Storage;
using Microsoft.Data.Sqlite;

namespace DeveMobileLPR.RdwDownloader;

internal sealed class RdwImportService(IRdwSource source)
{
    private readonly IRdwSource _source = source ?? throw new ArgumentNullException(nameof(source));

    public async Task<ImportResult> RunAsync(
        RdwDownloaderOptions options,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var outputPath = Path.GetFullPath(options.OutputPath);
        var buildPath = outputPath + ".building";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("Output path has no parent directory."));

        if (options.Restart)
        {
            DeletePartialDatabase(buildPath);
        }

        var vehicleSnapshotTask = _source.GetSnapshotAsync(RdwDatasets.Vehicles, cancellationToken);
        var fuelSnapshotTask = _source.GetSnapshotAsync(RdwDatasets.Fuels, cancellationToken);
        var vehicleCountTask = _source.GetRowCountAsync(RdwDatasets.Vehicles, cancellationToken);
        var fuelCountTask = _source.GetRowCountAsync(RdwDatasets.Fuels, cancellationToken);
        await Task.WhenAll(vehicleSnapshotTask, fuelSnapshotTask, vehicleCountTask, fuelCountTask).ConfigureAwait(false);
        var vehicleSnapshot = await vehicleSnapshotTask.ConfigureAwait(false);
        var fuelSnapshot = await fuelSnapshotTask.ConfigureAwait(false);
        var vehicleCount = await vehicleCountTask.ConfigureAwait(false);
        var fuelCount = await fuelCountTask.ConfigureAwait(false);
        ValidateSnapshot(vehicleSnapshot, RdwDatasets.RequiredVehicleFields);
        ValidateSnapshot(fuelSnapshot, RdwDatasets.RequiredFuelFields);

        (long VehicleRows, long FuelRows, long VehiclesWithFuel) finalized;
        await using (var database = await RdwDatabaseBuilder.OpenAsync(
            buildPath,
            vehicleSnapshot,
            fuelSnapshot,
            options.SampleRows,
            cancellationToken).ConfigureAwait(false))
        {
            var stopwatch = Stopwatch.StartNew();
            await ImportVehiclesAsync(database, options, vehicleCount, stopwatch, progress, cancellationToken).ConfigureAwait(false);
            await ImportFuelsAsync(database, options, fuelCount, stopwatch, progress, cancellationToken).ConfigureAwait(false);

            var currentVehicleSnapshotTask = _source.GetSnapshotAsync(RdwDatasets.Vehicles, cancellationToken);
            var currentFuelSnapshotTask = _source.GetSnapshotAsync(RdwDatasets.Fuels, cancellationToken);
            await Task.WhenAll(currentVehicleSnapshotTask, currentFuelSnapshotTask).ConfigureAwait(false);
            EnsureUnchanged(vehicleSnapshot, await currentVehicleSnapshotTask.ConfigureAwait(false));
            EnsureUnchanged(fuelSnapshot, await currentFuelSnapshotTask.ConfigureAwait(false));

            finalized = await database.FinalizeAsync(
                vehicleCount,
                fuelCount,
                options.SampleRows.HasValue,
                cancellationToken).ConfigureAwait(false);
        }

        var lookup = new RdwVehicleLookup(buildPath);
        await lookup.ValidateAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        File.Move(buildPath, outputPath, true);
        DeleteSidecar(buildPath + "-wal");
        DeleteSidecar(buildPath + "-shm");

        return new ImportResult(
            outputPath,
            finalized.VehicleRows,
            finalized.FuelRows,
            finalized.VehiclesWithFuel,
            options.SampleRows.HasValue);
    }

    private async Task ImportVehiclesAsync(
        RdwDatabaseBuilder database,
        RdwDownloaderOptions options,
        long sourceCount,
        Stopwatch stopwatch,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var expected = Math.Min(sourceCount, options.SampleRows ?? long.MaxValue);
        var state = await database.GetStateAsync(RdwDatasets.Vehicles, cancellationToken).ConfigureAwait(false);
        var resumed = state.ImportedRows > 0;
        while (!state.Completed)
        {
            var requestSize = RequestSize(options.PageSize, expected, state.ImportedRows);
            if (requestSize == 0)
            {
                database.CommitVehiclePage([], completed: true);
            }
            else
            {
                var rows = await _source.GetVehiclePageAsync(state.LastPlate, requestSize, cancellationToken).ConfigureAwait(false);
                var completed = rows.Count < requestSize || state.ImportedRows + rows.Count >= expected;
                database.CommitVehiclePage(rows, completed);
            }

            state = await database.GetStateAsync(RdwDatasets.Vehicles, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ImportProgress("vehicles", state.ImportedRows, expected, stopwatch.Elapsed, resumed));
        }
    }

    private async Task ImportFuelsAsync(
        RdwDatabaseBuilder database,
        RdwDownloaderOptions options,
        long sourceCount,
        Stopwatch stopwatch,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var expected = Math.Min(sourceCount, options.SampleRows ?? long.MaxValue);
        var state = await database.GetStateAsync(RdwDatasets.Fuels, cancellationToken).ConfigureAwait(false);
        var resumed = state.ImportedRows > 0;
        while (!state.Completed)
        {
            var requestSize = RequestSize(options.PageSize, expected, state.ImportedRows);
            if (requestSize == 0)
            {
                database.CommitFuelPage([], completed: true);
            }
            else
            {
                var rows = await _source.GetFuelPageAsync(
                    state.LastPlate,
                    state.LastSequence,
                    requestSize,
                    cancellationToken).ConfigureAwait(false);
                var completed = rows.Count < requestSize || state.ImportedRows + rows.Count >= expected;
                database.CommitFuelPage(rows, completed);
            }

            state = await database.GetStateAsync(RdwDatasets.Fuels, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ImportProgress("fuels", state.ImportedRows, expected, stopwatch.Elapsed, resumed));
        }
    }

    private static int RequestSize(int pageSize, long expected, long imported)
    {
        var remaining = expected - imported;
        return remaining <= 0 ? 0 : (int)Math.Min(pageSize, remaining);
    }

    private static void ValidateSnapshot(DatasetSnapshot snapshot, IReadOnlySet<string> requiredFields)
    {
        var missing = requiredFields.Where(field => !snapshot.Fields.Contains(field)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"Official RDW dataset {snapshot.Id} no longer exposes required fields: {string.Join(", ", missing)}.");
        }
    }

    private static void EnsureUnchanged(DatasetSnapshot initial, DatasetSnapshot current)
    {
        if (initial.RowsUpdatedAt != current.RowsUpdatedAt)
        {
            throw new InvalidDataException(
                $"RDW dataset {initial.Id} changed while it was being downloaded. " +
                "The partial database was preserved; use --restart to create a consistent snapshot.");
        }
    }

    internal static void DeletePartialDatabase(string buildPath)
    {
        DeleteSidecar(buildPath);
        DeleteSidecar(buildPath + "-wal");
        DeleteSidecar(buildPath + "-shm");
    }

    private static void DeleteSidecar(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
