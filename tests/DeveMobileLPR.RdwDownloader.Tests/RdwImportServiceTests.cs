using DeveMobileLPR.RdwDownloader;
using DeveMobileLPR.Storage;
using Microsoft.Data.Sqlite;

namespace DeveMobileLPR.Tests;

public sealed class RdwImportServiceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DeveMobileLPR-RDW-{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_BuildsValidatedDatabaseConsumedByMobileLookup()
    {
        var output = Path.Combine(_directory, "rdw.sqlite");
        var source = FakeSource.Complete();
        var result = await new RdwImportService(source).RunAsync(
            Options(output, pageSize: 2),
            null,
            CancellationToken.None);

        Assert.Equal(3L, result.VehicleRows);
        Assert.Equal(4L, result.FuelRows);
        Assert.Equal(2L, result.VehiclesWithFuel);
        Assert.False(result.IsSample);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(output + ".building"));

        var lookup = new SqliteRdwVehicleLookup(output);
        var vehicle = await lookup.FindAsync("ab-12-cd", CancellationToken.None);
        Assert.NotNull(vehicle);
        Assert.Equal("Audi", vehicle.Make);
        Assert.Equal("A6", vehicle.Model);
        Assert.Equal(85_250m, vehicle.CatalogPrice);
        Assert.Equal(2024, vehicle.RegistrationYear);
        Assert.Equal("Benzine / Elektriciteit", vehicle.FuelDescription);
        Assert.Equal("Personenauto", vehicle.BodyType);

        await using var connection = new SqliteConnection($"Data Source={output};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var metadata = connection.CreateCommand();
        metadata.CommandText = "SELECT imported_vehicle_rows, imported_fuel_rows, is_sample FROM rdw_import_metadata;";
        await using var reader = await metadata.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(3L, reader.GetInt64(0));
        Assert.Equal(4L, reader.GetInt64(1));
        Assert.Equal(0L, reader.GetInt64(2));
    }

    [Fact]
    public async Task OpenAsync_ResumesCommittedCursorAndRejectsChangedSourceSnapshot()
    {
        var partial = Path.Combine(_directory, "rdw.sqlite.building");
        var source = FakeSource.Complete();
        var vehicleSnapshot = await source.GetSnapshotAsync(RdwDatasets.Vehicles, CancellationToken.None);
        var fuelSnapshot = await source.GetSnapshotAsync(RdwDatasets.Fuels, CancellationToken.None);

        await using (var database = await RdwDatabaseBuilder.OpenAsync(
            partial,
            vehicleSnapshot,
            fuelSnapshot,
            null,
            CancellationToken.None))
        {
            database.CommitVehiclePage(source.Vehicles.Take(2).ToArray(), completed: false);
        }

        await using (var resumed = await RdwDatabaseBuilder.OpenAsync(
            partial,
            vehicleSnapshot,
            fuelSnapshot,
            null,
            CancellationToken.None))
        {
            var state = await resumed.GetStateAsync(RdwDatasets.Vehicles, CancellationToken.None);
            Assert.Equal(2L, state.ImportedRows);
            Assert.Equal("EF34GH", state.LastPlate);
            Assert.False(state.Completed);
        }

        var changed = vehicleSnapshot with { RowsUpdatedAt = vehicleSnapshot.RowsUpdatedAt + 1 };
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => RdwDatabaseBuilder.OpenAsync(
            partial,
            changed,
            fuelSnapshot,
            null,
            CancellationToken.None));
        Assert.Contains("--restart", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DoesNotReplacePreviousOutputWhenDownloadFails()
    {
        var output = Path.Combine(_directory, "rdw.sqlite");
        await File.WriteAllTextAsync(output, "previous database marker");
        var source = FakeSource.Complete();
        source.ThrowOnFuelPage = true;

        await Assert.ThrowsAsync<IOException>(() => new RdwImportService(source).RunAsync(
            Options(output, pageSize: 2),
            null,
            CancellationToken.None));

        Assert.Equal("previous database marker", await File.ReadAllTextAsync(output));
        Assert.True(File.Exists(output + ".building"));
    }

    private static RdwDownloaderOptions Options(string output, int pageSize) =>
        new(output, null, pageSize, null, Restart: false, ShowHelp: false);

    private sealed class FakeSource : IRdwSource
    {
        private readonly DatasetSnapshot _vehiclesSnapshot = new(
            RdwDatasets.Vehicles,
            "Vehicles",
            1_720_000_000,
            RdwDatasets.RequiredVehicleFields);
        private readonly DatasetSnapshot _fuelsSnapshot = new(
            RdwDatasets.Fuels,
            "Fuels",
            1_720_000_001,
            RdwDatasets.RequiredFuelFields);

        public List<VehicleSourceRow> Vehicles { get; } =
        [
            new("AB12CD", "Audi", "A6", 85_250, 2024, "Personenauto"),
            new("EF34GH", "Volvo", "V60", 62_000, 2021, "Stationwagen"),
            new("XY99ZZ", "Daf", "XF", null, 2019, "Vrachtwagen")
        ];

        public List<FuelSourceRow> Fuels { get; } =
        [
            new("AB12CD", "1", "Benzine"),
            new("AB12CD", "2", "Elektriciteit"),
            new("EF34GH", "1", "Diesel"),
            new("NO00NE", "1", "Benzine")
        ];

        public bool ThrowOnFuelPage { get; set; }

        public static FakeSource Complete() => new();

        public Task<DatasetSnapshot> GetSnapshotAsync(string datasetId, CancellationToken cancellationToken) =>
            Task.FromResult(datasetId == RdwDatasets.Vehicles ? _vehiclesSnapshot : _fuelsSnapshot);

        public Task<long> GetRowCountAsync(string datasetId, CancellationToken cancellationToken) =>
            Task.FromResult(datasetId == RdwDatasets.Vehicles ? (long)Vehicles.Count : Fuels.Count);

        public Task<IReadOnlyList<VehicleSourceRow>> GetVehiclePageAsync(
            string? afterPlate,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VehicleSourceRow>>(Vehicles
                .Where(row => afterPlate is null || string.CompareOrdinal(row.CursorPlate, afterPlate) > 0)
                .Take(limit)
                .ToArray());

        public Task<IReadOnlyList<FuelSourceRow>> GetFuelPageAsync(
            string? afterPlate,
            string? afterSequence,
            int limit,
            CancellationToken cancellationToken)
        {
            if (ThrowOnFuelPage)
            {
                throw new IOException("Simulated fuel download failure.");
            }

            return Task.FromResult<IReadOnlyList<FuelSourceRow>>(Fuels
                .Where(row => afterPlate is null ||
                    string.CompareOrdinal(row.CursorPlate, afterPlate) > 0 ||
                    (row.CursorPlate == afterPlate && string.CompareOrdinal(row.CursorSequence, afterSequence!) > 0))
                .Take(limit)
                .ToArray());
        }
    }
}
