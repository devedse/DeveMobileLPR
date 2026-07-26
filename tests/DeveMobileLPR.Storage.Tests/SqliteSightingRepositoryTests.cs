using DeveMobileLPR.Geometry;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.Tests;

public sealed class SqliteSightingRepositoryTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"DeveMobileLPR-{Guid.NewGuid():N}.sqlite");
    private SqliteSightingRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _repository = new SqliteSightingRepository(_databasePath);
        await _repository.InitializeAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task AddOrMergeAsync_MergesNearbyObservationsAndKeepsVehicleData()
    {
        var first = Confirmed("AB1234", DateTimeOffset.UtcNow, 3);
        var vehicle = new VehicleRecord("AB1234", "Audi", "A6", 85_000m, 2024, "Benzine", "Personenauto");
        await _repository.AddOrMergeAsync(first, new GeoPoint(52.1, 5.1, 4), vehicle, null, CancellationToken.None);
        await _repository.AddOrMergeAsync(
            Confirmed("AB1234", first.LastSeenAt.AddSeconds(20), 4),
            null,
            null,
            null,
            CancellationToken.None);

        var sightings = await _repository.FindByPlateAsync("ab-12-34", CancellationToken.None);

        var sighting = Assert.Single(sightings);
        Assert.Equal(7, sighting.ObservationCount);
        Assert.Equal("Audi", sighting.Vehicle?.Make);
        Assert.Equal(85_000m, sighting.Vehicle?.CatalogPrice);
        Assert.NotNull(await _repository.GetMostExpensiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Confirmation_WithImportedRdwVehicle_PersistsCatalogDetails()
    {
        var rdwPath = Path.Combine(Path.GetTempPath(), $"DeveMobileLPR-rdw-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={rdwPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE rdw_vehicle_data (
                        normalized_plate TEXT PRIMARY KEY,
                        make TEXT,
                        model TEXT,
                        catalog_price INTEGER,
                        registration_year INTEGER,
                        fuel_description TEXT,
                        body_type TEXT);
                    INSERT INTO rdw_vehicle_data VALUES
                        ('G694NT', 'TESLA', 'MODEL 3', 59350, 2019, 'Elektriciteit', 'sedan');
                    CREATE VIEW rdw_vehicles AS
                        SELECT normalized_plate, make, model, catalog_price,
                               registration_year, fuel_description, body_type
                        FROM rdw_vehicle_data;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var lookup = new SqliteRdwVehicleLookup(rdwPath);
            await lookup.ValidateAsync(CancellationToken.None);
            var vehicle = await lookup.FindAsync("G-694-NT", CancellationToken.None);
            var time = DateTimeOffset.UtcNow;
            var sighting = await _repository.AddOrMergeAsync(
                Confirmed("G694NT", time, 3),
                new GeoPoint(52.1, 5.1, 4),
                vehicle,
                null,
                CancellationToken.None);

            Assert.NotNull(vehicle);
            Assert.Equal("TESLA", sighting.Vehicle?.Make);
            Assert.Equal("MODEL 3", sighting.Vehicle?.Model);
            Assert.Equal(59_350m, sighting.Vehicle?.CatalogPrice);
            Assert.Equal("Elektriciteit", sighting.Vehicle?.FuelDescription);
            Assert.Equal("sedan", sighting.Vehicle?.BodyType);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(rdwPath)) File.Delete(rdwPath);
        }
    }

    [Fact]
    public async Task Trips_KeepSightingsSeparateAndCalculateDistanceAndStatistics()
    {
        var startedAt = new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.Zero);
        var trip = await _repository.StartTripAsync(startedAt, new GeoPoint(52.0907, 5.1214, 4), CancellationToken.None);
        await _repository.AddTripPointAsync(trip.Id, startedAt, new GeoPoint(52.0907, 5.1214, 4), CancellationToken.None);
        await _repository.AddTripPointAsync(trip.Id, startedAt.AddMinutes(1), new GeoPoint(52.0917, 5.1214, 4), CancellationToken.None);
        var vehicle = new VehicleRecord("AB1234", "Audi", "A6", 85_000m, 2024, "Benzine", "Personenauto");
        var sighting = await _repository.AddOrMergeAsync(Confirmed("AB1234", startedAt.AddSeconds(30), 3), new GeoPoint(52.091, 5.1214, 3), vehicle, trip.Id, CancellationToken.None);
        var ended = await _repository.EndTripAsync(trip.Id, startedAt.AddMinutes(2), new GeoPoint(52.0917, 5.1214, 4), CancellationToken.None);

        Assert.Equal(trip.Id, sighting.TripId);
        Assert.InRange(ended.DistanceMeters, 100, 125);
        Assert.Equal(1, ended.SightingCount);
        Assert.Equal(1, ended.UniqueVehicleCount);
        Assert.Equal(85_000m, ended.MostExpensiveCatalogPrice);
        Assert.Equal(2, (await _repository.GetTripPointsAsync(trip.Id, CancellationToken.None)).Count);

        var stats = await _repository.GetStatisticsAsync(startedAt.AddHours(-1), startedAt.AddHours(2), CancellationToken.None);
        Assert.Equal(1, stats.TripCount);
        Assert.Equal(1, stats.SightingCount);
        Assert.Equal(1, stats.UniqueVehicleCount);
        Assert.Equal("AB1234", stats.MostExpensiveSighting?.NormalizedPlate);
    }

    [Fact]
    public async Task VehicleHistory_GroupsRepeatedSightingsAcrossTrips()
    {
        var now = DateTimeOffset.UtcNow;
        var firstTrip = await _repository.StartTripAsync(now, null, CancellationToken.None);
        await _repository.AddOrMergeAsync(Confirmed("AB1234", now, 3), null, null, firstTrip.Id, CancellationToken.None);
        await _repository.EndTripAsync(firstTrip.Id, now.AddMinutes(1), null, CancellationToken.None);
        var secondTrip = await _repository.StartTripAsync(now.AddHours(1), null, CancellationToken.None);
        await _repository.AddOrMergeAsync(Confirmed("AB1234", now.AddHours(1), 3), null, null, secondTrip.Id, CancellationToken.None);
        await _repository.EndTripAsync(secondTrip.Id, now.AddHours(1).AddMinutes(1), null, CancellationToken.None);

        var vehicle = Assert.Single(await _repository.GetVehicleHistoryAsync("AB-12-34", 100, CancellationToken.None));
        Assert.Equal(2, vehicle.SightingCount);
        Assert.Equal(2, vehicle.TripCount);

        await _repository.DeleteHistoryAsync(CancellationToken.None);
        Assert.Empty(await _repository.GetTripsAsync(100, CancellationToken.None));
        Assert.Empty(await _repository.GetRecentAsync(100, CancellationToken.None));
    }

    [Fact]
    public async Task InitializeAsync_MigratesVersionOneSightingsWithoutDataLoss()
    {
        var legacyPath = Path.Combine(Path.GetTempPath(), $"DeveMobileLPR-legacy-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE sightings (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, normalized_plate TEXT NOT NULL, display_plate TEXT NOT NULL,
                        region TEXT NULL, first_seen_at TEXT NOT NULL, last_seen_at TEXT NOT NULL, confidence REAL NOT NULL,
                        observation_count INTEGER NOT NULL, latitude REAL NULL, longitude REAL NULL, location_accuracy_meters REAL NULL,
                        make TEXT NULL, model TEXT NULL, catalog_price NUMERIC NULL, registration_year INTEGER NULL,
                        fuel_description TEXT NULL, body_type TEXT NULL);
                    INSERT INTO sightings (normalized_plate, display_plate, first_seen_at, last_seen_at, confidence, observation_count)
                    VALUES ('AB1234', 'AB-12-34', '2026-07-26T08:00:00.0000000+00:00', '2026-07-26T08:00:01.0000000+00:00', 0.95, 3);
                    PRAGMA user_version = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var repository = new SqliteSightingRepository(legacyPath);
            await repository.InitializeAsync(CancellationToken.None);

            var sighting = Assert.Single(await repository.GetAllSightingsAsync(CancellationToken.None));
            Assert.Equal("AB1234", sighting.NormalizedPlate);
            Assert.Null(sighting.TripId);
            await using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyPath}");
            await verify.OpenAsync();
            await using var version = verify.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(2L, (long)(await version.ExecuteScalarAsync())!);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
        }
    }

    private static ConfirmedPlate Confirmed(string plate, DateTimeOffset time, int observations) => new(
        Guid.NewGuid(),
        time.AddSeconds(-1),
        time,
        new BoundingBox(10, 10, 100, 40),
        new ConsensusResult(plate, PlateText.FormatDutchPlate(plate), "Netherlands", 0.95f, observations));
}
