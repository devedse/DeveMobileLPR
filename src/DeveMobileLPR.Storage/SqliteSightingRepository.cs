using DeveMobileLPR.Geometry;
using DeveMobileLPR.Recognition;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DeveMobileLPR.Storage;

public sealed class SqliteSightingRepository : ISightingRepository
{
    private const string MigrationHistoryTable = "__EFMigrationsHistory";
    private readonly string _databasePath;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly TimeSpan _mergeWindow;

    public SqliteSightingRepository(string databasePath, TimeSpan? mergeWindow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ConnectionString(_databasePath))
            .Options;
        _mergeWindow = mergeWindow ?? TimeSpan.FromMinutes(3);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_databasePath) && !await HasMigrationHistoryAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The local database at '{_databasePath}' uses an incompatible pre-EF Core schema. Delete the file and restart; no external data depends on it.");
        }

        await using var db = CreateContext();
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await RecoverInterruptedTripsAsync(db, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Sighting> AddOrMergeAsync(
        ConfirmedPlate plate,
        GeoPoint? location,
        VehicleRecord? vehicle,
        long? tripId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plate);
        await using var db = CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existing = await FindMergeCandidateAsync(
            db,
            plate.Consensus.NormalizedPlate,
            plate.FirstSeenAt - _mergeWindow,
            tripId,
            cancellationToken).ConfigureAwait(false);

        SightingEntity entity;
        if (existing is not null)
        {
            existing.LastSeenAt = plate.LastSeenAt;
            existing.Confidence = Math.Max(existing.Confidence, plate.Consensus.Confidence);
            existing.ObservationCount += plate.Consensus.ObservationCount;
            existing.Latitude ??= location?.Latitude;
            existing.Longitude ??= location?.Longitude;
            existing.LocationAccuracyMeters ??= location?.AccuracyMeters;
            existing.Region ??= plate.Consensus.Region;
            MergeVehicle(existing, vehicle);
            entity = existing;
        }
        else
        {
            entity = new SightingEntity
            {
                NormalizedPlate = plate.Consensus.NormalizedPlate,
                DisplayPlate = plate.Consensus.DisplayPlate,
                Region = plate.Consensus.Region,
                FirstSeenAt = plate.FirstSeenAt,
                LastSeenAt = plate.LastSeenAt,
                Confidence = plate.Consensus.Confidence,
                ObservationCount = plate.Consensus.ObservationCount,
                Latitude = location?.Latitude,
                Longitude = location?.Longitude,
                LocationAccuracyMeters = location?.AccuracyMeters,
                Make = vehicle?.Make,
                Model = vehicle?.Model,
                CatalogPrice = vehicle?.CatalogPrice,
                RegistrationYear = vehicle?.RegistrationYear,
                FuelDescription = vehicle?.FuelDescription,
                BodyType = vehicle?.BodyType,
                TripId = tripId
            };
            db.Sightings.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToSighting(entity);
    }

    public async Task<Sighting> ReviseAsync(
        long sightingId,
        ConfirmedPlate plate,
        VehicleRecord? vehicle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plate);
        await using var db = CreateContext();
        var entity = await db.Sightings.SingleOrDefaultAsync(s => s.Id == sightingId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Sighting {sightingId} does not exist.");
        entity.NormalizedPlate = plate.Consensus.NormalizedPlate;
        entity.DisplayPlate = plate.Consensus.DisplayPlate;
        entity.Region = plate.Consensus.Region;
        entity.LastSeenAt = plate.LastSeenAt;
        entity.Confidence = plate.Consensus.Confidence;
        entity.ObservationCount = plate.Consensus.ObservationCount;
        entity.Make = vehicle?.Make;
        entity.Model = vehicle?.Model;
        entity.CatalogPrice = vehicle?.CatalogPrice;
        entity.RegistrationYear = vehicle?.RegistrationYear;
        entity.FuelDescription = vehicle?.FuelDescription;
        entity.BodyType = vehicle?.BodyType;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToSighting(entity);
    }

    public async Task<Sighting> SetSnapshotReferenceAsync(
        long sightingId,
        string snapshotReference,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sightingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotReference);
        await using var db = CreateContext();
        var entity = await db.Sightings.SingleOrDefaultAsync(s => s.Id == sightingId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Sighting {sightingId} does not exist.");
        entity.SnapshotReference = snapshotReference;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToSighting(entity);
    }

    public async Task<TripSummary> StartTripAsync(DateTimeOffset startedAt, GeoPoint? location, CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var entity = new TripEntity
        {
            StartedAt = startedAt,
            StartLatitude = location?.Latitude,
            StartLongitude = location?.Longitude,
            StartAccuracyMeters = location?.AccuracyMeters
        };
        db.Trips.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await GetTripAsync(entity.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The newly created trip could not be read.");
    }

    public async Task<TripSummary> EndTripAsync(long tripId, DateTimeOffset endedAt, GeoPoint? location, CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var entity = await db.Trips.SingleOrDefaultAsync(t => t.Id == tripId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Trip {tripId} does not exist.");
        entity.EndedAt = endedAt;
        if (location is { } point)
        {
            entity.EndLatitude = point.Latitude;
            entity.EndLongitude = point.Longitude;
            entity.EndAccuracyMeters = point.AccuracyMeters;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await GetTripAsync(tripId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The ended trip could not be read.");
    }

    public async Task AddTripPointAsync(long tripId, DateTimeOffset recordedAt, GeoPoint location, CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var previous = await db.TripPoints.AsNoTracking()
            .Where(p => p.TripId == tripId)
            .OrderByDescending(p => p.RecordedAt)
            .Select(p => new { p.Latitude, p.Longitude, p.AccuracyMeters })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        db.TripPoints.Add(new TripPointEntity
        {
            TripId = tripId,
            RecordedAt = recordedAt,
            Latitude = location.Latitude,
            Longitude = location.Longitude,
            AccuracyMeters = location.AccuracyMeters
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (previous is not null)
        {
            var distance = GeoMath.DistanceMeters(
                new GeoPoint(previous.Latitude, previous.Longitude, previous.AccuracyMeters),
                location);
            await db.Trips.Where(t => t.Id == tripId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.DistanceMeters, t => t.DistanceMeters + distance), cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<TripSummary>> GetTripsAsync(int offset, int limit, CancellationToken cancellationToken) =>
        QueryTripsAsync(
            trips => trips
                .OrderByDescending(t => t.StartedAt)
                .ThenByDescending(t => t.Id)
                .Skip(Math.Max(0, offset))
                .Take(Math.Clamp(limit, 1, 1000)),
            cancellationToken);

    public async Task<TripSummary?> GetTripAsync(long tripId, CancellationToken cancellationToken)
    {
        var trips = await QueryTripsAsync(
            trips => trips.Where(t => t.Id == tripId),
            cancellationToken).ConfigureAwait(false);
        return trips.FirstOrDefault();
    }

    public async Task<IReadOnlyList<Sighting>> GetSightingsForTripAsync(long tripId, CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var sightings = await db.Sightings.AsNoTracking()
            .Where(s => s.TripId == tripId)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return sightings.Select(ToSighting).ToArray();
    }

    public async Task<IReadOnlyList<TripVehicleSummary>> GetVehiclesForTripAsync(long tripId, CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var tripStartedAt = await db.Trips.AsNoTracking()
            .Where(t => t.Id == tripId)
            .Select(t => (DateTimeOffset?)t.StartedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (tripStartedAt is null)
        {
            return [];
        }

        var rows = await db.Sightings.AsNoTracking()
            .Where(s => s.TripId == tripId)
            .GroupBy(s => s.NormalizedPlate)
            .Select(g => new TripVehicleRow(
                g.Key,
                g.Max(s => s.DisplayPlate)!,
                g.Min(s => s.FirstSeenAt),
                g.Max(s => s.LastSeenAt),
                g.Max(s => s.Confidence),
                g.Sum(s => s.ObservationCount),
                g.Count(),
                g.Max(s => s.Make),
                g.Max(s => s.Model),
                g.Max(s => s.CatalogPrice),
                g.Max(s => s.RegistrationYear),
                g.Max(s => s.FuelDescription),
                g.Max(s => s.BodyType),
                g.Where(s => s.Latitude != null && s.Longitude != null)
                    .OrderByDescending(s => s.LastSeenAt)
                    .Select(s => new LocationRow(s.Latitude!.Value, s.Longitude!.Value, s.LocationAccuracyMeters))
                    .FirstOrDefault(),
                g.Where(s => s.SnapshotReference != null)
                    .OrderByDescending(s => s.LastSeenAt)
                    .ThenByDescending(s => s.Id)
                    .Select(s => (string?)s.SnapshotReference)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var plates = rows.Select(row => row.NormalizedPlate).ToList();
        var earlierCounts = await db.Sightings.AsNoTracking()
            .Where(s => plates.Contains(s.NormalizedPlate) && s.LastSeenAt < tripStartedAt.Value)
            .GroupBy(s => s.NormalizedPlate)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(row => row.Key, row => row.Count, cancellationToken).ConfigureAwait(false);

        return rows.Select(row => new TripVehicleSummary(
            row.NormalizedPlate,
            row.DisplayPlate,
            row.FirstSeenAt,
            row.LastSeenAt,
            row.Confidence,
            row.ObservationCount,
            row.SightingCount,
            earlierCounts.GetValueOrDefault(row.NormalizedPlate),
            row.Make is null && row.Model is null && row.CatalogPrice is null
                ? null
                : new VehicleRecord(
                    row.NormalizedPlate,
                    row.Make,
                    row.Model,
                    row.CatalogPrice,
                    row.RegistrationYear,
                    row.FuelDescription,
                    row.BodyType),
            row.LastLocation is null
                ? null
                : new GeoPoint(row.LastLocation.Latitude, row.LastLocation.Longitude, row.LastLocation.AccuracyMeters),
            row.SnapshotReference)).ToArray();
    }

    public async Task<IReadOnlyList<TripPoint>> GetTripPointsAsync(long tripId, CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var points = await db.TripPoints.AsNoTracking()
            .Where(p => p.TripId == tripId)
            .OrderBy(p => p.RecordedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return points
            .Select(point => new TripPoint(
                point.Id,
                point.TripId,
                point.RecordedAt,
                new GeoPoint(point.Latitude, point.Longitude, point.AccuracyMeters)))
            .ToArray();
    }

    public async Task<IReadOnlyList<VehicleHistorySummary>> GetVehicleHistoryAsync(VehicleHistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = CreateContext();
        var grouped = SearchableSightings(db, query.Search)
            .GroupBy(s => s.NormalizedPlate)
            .Where(row => query.SeenSince == null || row.Max(s => s.LastSeenAt) >= query.SeenSince.Value)
            .Where(row => query.MinimumCatalogPrice == null || row.Max(s => s.CatalogPrice) > query.MinimumCatalogPrice.Value)
            .Select(g => new
            {
                g.Key,
                DisplayPlate = g.Max(s => s.DisplayPlate),
                FirstSeenAt = g.Min(s => s.FirstSeenAt),
                LastSeenAt = g.Max(s => s.LastSeenAt),
                SightingCount = g.Count(),
                Make = g.Max(s => s.Make),
                Model = g.Max(s => s.Model),
                CatalogPrice = g.Max(s => s.CatalogPrice),
                RegistrationYear = g.Max(s => s.RegistrationYear),
                FuelDescription = g.Max(s => s.FuelDescription),
                BodyType = g.Max(s => s.BodyType),
                LastLocation = g.Where(s => s.Latitude != null && s.Longitude != null)
                    .OrderByDescending(s => s.LastSeenAt)
                    .Select(s => new LocationRow(s.Latitude!.Value, s.Longitude!.Value, s.LocationAccuracyMeters))
                    .FirstOrDefault(),
                SnapshotReference = g.Where(s => s.SnapshotReference != null)
                    .OrderByDescending(s => s.LastSeenAt)
                    .ThenByDescending(s => s.Id)
                    .Select(s => (string?)s.SnapshotReference)
                    .FirstOrDefault()
            });

        var ordered = query.Sort == VehicleHistorySort.HighestValue
            ? grouped.OrderByDescending(row => row.CatalogPrice).ThenByDescending(row => row.LastSeenAt).ThenBy(row => row.Key)
            : grouped.OrderByDescending(row => row.LastSeenAt).ThenBy(row => row.Key);

        var rows = await ordered
            .Skip(Math.Max(0, query.Offset))
            .Take(Math.Clamp(query.Limit, 1, 1000))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var plates = rows.Select(row => row.Key).ToList();
        var tripCounts = plates.Count == 0
            ? []
            : await db.Sightings.AsNoTracking()
                .Where(s => plates.Contains(s.NormalizedPlate) && s.TripId != null)
                .GroupBy(s => s.NormalizedPlate)
                .Select(g => new { g.Key, Trips = g.Select(s => s.TripId).Distinct().Count() })
                .ToDictionaryAsync(row => row.Key, row => row.Trips, cancellationToken).ConfigureAwait(false);

        return rows.Select(row => new VehicleHistorySummary(
            row.Key,
            row.DisplayPlate!,
            row.FirstSeenAt,
            row.LastSeenAt,
            row.SightingCount,
            tripCounts.TryGetValue(row.Key, out var trips) ? trips : 0,
            row.Make is null && row.Model is null && row.CatalogPrice is null
                ? null
                : new VehicleRecord(
                    row.Key,
                    row.Make,
                    row.Model,
                    row.CatalogPrice,
                    row.RegistrationYear,
                    row.FuelDescription,
                    row.BodyType),
            row.LastLocation is null
                ? null
                : new GeoPoint(row.LastLocation.Latitude, row.LastLocation.Longitude, row.LastLocation.AccuracyMeters),
            row.SnapshotReference)).ToArray();
    }

    public async Task<HistoryStatistics> GetStatisticsAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var tripCount = await db.Trips.AsNoTracking()
            .CountAsync(t => t.StartedAt < until && (t.EndedAt == null || t.EndedAt >= from), cancellationToken).ConfigureAwait(false);
        var sightingCount = await db.Sightings.AsNoTracking()
            .CountAsync(s => s.LastSeenAt >= from && s.LastSeenAt < until, cancellationToken).ConfigureAwait(false);
        var uniqueCount = await db.Sightings.AsNoTracking()
            .Where(s => s.LastSeenAt >= from && s.LastSeenAt < until)
            .Select(s => s.NormalizedPlate)
            .Distinct()
            .CountAsync(cancellationToken).ConfigureAwait(false);
        var distance = await db.Trips.AsNoTracking()
            .Where(t => t.StartedAt < until && (t.EndedAt == null || t.EndedAt >= from))
            .SumAsync(t => t.DistanceMeters, cancellationToken).ConfigureAwait(false);
        var mostExpensive = await db.Sightings.AsNoTracking()
            .Where(s => s.LastSeenAt >= from && s.LastSeenAt < until && s.CatalogPrice != null)
            .OrderByDescending(s => s.CatalogPrice)
            .ThenByDescending(s => s.LastSeenAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return new HistoryStatistics(
            tripCount,
            sightingCount,
            uniqueCount,
            distance,
            mostExpensive is null ? null : ToSighting(mostExpensive));
    }

    public async Task<IReadOnlyList<Sighting>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var sightings = await db.Sightings.AsNoTracking()
            .OrderByDescending(s => s.LastSeenAt)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return sightings.Select(ToSighting).ToArray();
    }

    public async Task<IReadOnlyList<Sighting>> GetAllSightingsAsync(CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var sightings = await db.Sightings.AsNoTracking()
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return sightings.Select(ToSighting).ToArray();
    }

    public async Task<IReadOnlyList<Sighting>> FindByPlateAsync(string normalizedPlate, CancellationToken cancellationToken)
    {
        var plate = PlateText.Normalize(normalizedPlate);
        await using var db = CreateContext();
        var sightings = await db.Sightings.AsNoTracking()
            .Where(s => s.NormalizedPlate == plate)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return sightings.Select(ToSighting).ToArray();
    }

    public async Task<Sighting?> GetMostExpensiveAsync(CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var sighting = await db.Sightings.AsNoTracking()
            .Where(s => s.CatalogPrice != null)
            .OrderByDescending(s => s.CatalogPrice)
            .ThenByDescending(s => s.LastSeenAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return sighting is null ? null : ToSighting(sighting);
    }

    public async Task DeleteHistoryAsync(CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.TripPoints.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Sightings.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Trips.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IQueryable<SightingEntity> SearchableSightings(AppDbContext db, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return db.Sightings.AsNoTracking();
        }

        var platePattern = $"%{EscapeLike(PlateText.Normalize(search))}%";
        var textPattern = $"%{EscapeLike(search.Trim())}%";
        return db.Sightings.AsNoTracking().Where(s =>
            EF.Functions.Like(s.NormalizedPlate, platePattern, "\\")
            || EF.Functions.Like(s.Make, textPattern, "\\")
            || EF.Functions.Like(s.Model, textPattern, "\\"));
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static async Task<SightingEntity?> FindMergeCandidateAsync(
        AppDbContext db,
        string normalizedPlate,
        DateTimeOffset cutoff,
        long? tripId,
        CancellationToken cancellationToken)
    {
        var query = db.Sightings.Where(s => s.NormalizedPlate == normalizedPlate && s.LastSeenAt >= cutoff);
        query = tripId is null
            ? query.Where(s => s.TripId == null)
            : query.Where(s => s.TripId == tripId);
        return await query
            .OrderByDescending(s => s.LastSeenAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void MergeVehicle(SightingEntity entity, VehicleRecord? vehicle)
    {
        if (vehicle is null)
        {
            return;
        }

        entity.Make ??= vehicle.Make;
        entity.Model ??= vehicle.Model;
        entity.CatalogPrice ??= vehicle.CatalogPrice;
        entity.RegistrationYear ??= vehicle.RegistrationYear;
        entity.FuelDescription ??= vehicle.FuelDescription;
        entity.BodyType ??= vehicle.BodyType;
    }

    private async Task<IReadOnlyList<TripSummary>> QueryTripsAsync(
        Func<IQueryable<TripEntity>, IQueryable<TripEntity>> apply,
        CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var rows = await apply(db.Trips.AsNoTracking())
            .Select(t => new TripRow(
                t.Id,
                t.StartedAt,
                t.EndedAt,
                t.DistanceMeters,
                t.StartLatitude,
                t.StartLongitude,
                t.StartAccuracyMeters,
                t.EndLatitude,
                t.EndLongitude,
                t.EndAccuracyMeters))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return [];
        }

        var tripIds = rows.Select(row => row.Id).ToList();
        var aggregates = await db.Sightings.AsNoTracking()
            .Where(s => s.TripId != null && tripIds.Contains(s.TripId.Value))
            .GroupBy(s => s.TripId!.Value)
            .Select(g => new TripAggregates(
                g.Key,
                g.Count(),
                g.Select(s => s.NormalizedPlate).Distinct().Count(),
                g.Where(s => s.CatalogPrice != null)
                    .OrderByDescending(s => s.CatalogPrice)
                    .Select(s => (decimal?)s.CatalogPrice)
                    .FirstOrDefault(),
                g.Where(s => s.CatalogPrice != null)
                    .OrderByDescending(s => s.CatalogPrice)
                    .Select(s => (string?)s.DisplayPlate)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byTrip = aggregates.ToDictionary(aggregate => aggregate.Id);

        return rows.Select(row =>
        {
            byTrip.TryGetValue(row.Id, out var aggregate);
            return new TripSummary(
                row.Id,
                row.StartedAt,
                row.EndedAt,
                row.DistanceMeters,
                aggregate?.SightingCount ?? 0,
                aggregate?.UniqueVehicleCount ?? 0,
                aggregate?.MostExpensiveCatalogPrice,
                aggregate?.MostExpensiveDisplayPlate,
                ReadLocation(row.StartLatitude, row.StartLongitude, row.StartAccuracyMeters),
                ReadLocation(row.EndLatitude, row.EndLongitude, row.EndAccuracyMeters));
        }).ToArray();
    }

    private static async Task RecoverInterruptedTripsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var interrupted = await db.Trips.AsNoTracking()
            .Where(t => t.EndedAt == null)
            .Select(t => new
            {
                t.Id,
                t.StartedAt,
                LastPointAt = t.Points.Max(p => (DateTimeOffset?)p.RecordedAt),
                LastSightingAt = t.Sightings.Max(s => (DateTimeOffset?)s.LastSeenAt)
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var trip in interrupted)
        {
            await db.Trips.Where(t => t.Id == trip.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(t => t.EndedAt, trip.LastPointAt ?? trip.LastSightingAt ?? trip.StartedAt),
                    cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> HasMigrationHistoryAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString(_databasePath));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{MigrationHistoryTable}';";
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != 0;
    }

    private AppDbContext CreateContext() => new(_options);

    private static string ConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

    private static GeoPoint? ReadLocation(double? latitude, double? longitude, float? accuracy) =>
        latitude is double lat && longitude is double lon
            ? new GeoPoint(lat, lon, accuracy)
            : null;

    private static Sighting ToSighting(SightingEntity entity) => new(
        entity.Id,
        entity.NormalizedPlate,
        entity.DisplayPlate,
        entity.Region,
        entity.FirstSeenAt,
        entity.LastSeenAt,
        entity.Confidence,
        entity.ObservationCount,
        ReadLocation(entity.Latitude, entity.Longitude, entity.LocationAccuracyMeters),
        entity.Make is null && entity.Model is null && entity.CatalogPrice is null
            ? null
            : new VehicleRecord(
                entity.NormalizedPlate,
                entity.Make,
                entity.Model,
                entity.CatalogPrice,
                entity.RegistrationYear,
                entity.FuelDescription,
                entity.BodyType))
    {
        TripId = entity.TripId,
        SnapshotReference = entity.SnapshotReference
    };

    private sealed record TripRow(
        long Id,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        double DistanceMeters,
        double? StartLatitude,
        double? StartLongitude,
        float? StartAccuracyMeters,
        double? EndLatitude,
        double? EndLongitude,
        float? EndAccuracyMeters);

    private sealed record TripAggregates(
        long Id,
        int SightingCount,
        int UniqueVehicleCount,
        decimal? MostExpensiveCatalogPrice,
        string? MostExpensiveDisplayPlate);

    private sealed record TripVehicleRow(
        string NormalizedPlate,
        string DisplayPlate,
        DateTimeOffset FirstSeenAt,
        DateTimeOffset LastSeenAt,
        float Confidence,
        int ObservationCount,
        int SightingCount,
        string? Make,
        string? Model,
        decimal? CatalogPrice,
        int? RegistrationYear,
        string? FuelDescription,
        string? BodyType,
        LocationRow? LastLocation,
        string? SnapshotReference);

    private sealed record LocationRow(double Latitude, double Longitude, float? AccuracyMeters);
}
