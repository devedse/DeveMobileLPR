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
        await _repository.AddOrMergeAsync(first, new GeoPoint(52.1, 5.1, 4), vehicle, CancellationToken.None);
        await _repository.AddOrMergeAsync(
            Confirmed("AB1234", first.LastSeenAt.AddSeconds(20), 4),
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

    private static ConfirmedPlate Confirmed(string plate, DateTimeOffset time, int observations) => new(
        Guid.NewGuid(),
        time.AddSeconds(-1),
        time,
        new BoundingBox(10, 10, 100, 40),
        new ConsensusResult(plate, "AB-12-34", "Netherlands", 0.95f, observations));
}
