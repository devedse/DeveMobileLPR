using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DeveMobileLPR.Storage;

/// <summary>EF Core Code First implementation of the recognition history store.</summary>
public sealed class SightingRepository : ISightingRepository
{
    private const int MaximumPageSize = 1000;

    private readonly IDbContextFactory<LprDbContext> _contexts;
    private readonly TimeSpan _mergeWindow;

    public SightingRepository(IDbContextFactory<LprDbContext> contexts, TimeSpan? mergeWindow = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        _contexts = contexts;
        _mergeWindow = mergeWindow ?? TimeSpan.FromMinutes(3);
    }

    public SightingRepository(string databasePath, TimeSpan? mergeWindow = null)
        : this(new LprDbContextFactory(databasePath), mergeWindow)
    {
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = _contexts.CreateDbContext();
        if (await IsPreEfCoreDatabaseAsync(context, cancellationToken).ConfigureAwait(false))
        {
            // The history is a local cache that a later drive rebuilds, so a database written by the
            // hand-rolled schema is discarded rather than migrated into the Code First model.
            SqliteConnection.ClearAllPools();
            await context.Database.EnsureDeletedAsync(cancellationToken).ConfigureAwait(false);
        }

        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await CloseInterruptedTripsAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Sighting> AddOrMergeAsync(
        ConfirmedPlate plate,
        GeoPoint? location,
        VehicleRecord? vehicle,
        long? tripId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plate);
        var consensus = plate.Consensus;
        var normalizedPlate = consensus.NormalizedPlate;
        var cutoff = plate.FirstSeenAt - _mergeWindow;

        await using var context = _contexts.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sighting = await context.Sightings
            .Where(model => model.NormalizedPlate == normalizedPlate
                && model.LastSeenAt >= cutoff
                && model.TripId == tripId)
            .OrderByDescending(model => model.LastSeenAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (sighting is null)
        {
            sighting = new SightingModel
            {
                NormalizedPlate = normalizedPlate,
                DisplayPlate = consensus.DisplayPlate,
                Region = consensus.Region,
                FirstSeenAt = plate.FirstSeenAt,
                LastSeenAt = plate.LastSeenAt,
                Confidence = consensus.Confidence,
                ObservationCount = consensus.ObservationCount,
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
            context.Sightings.Add(sighting);
        }
        else
        {
            // Merging keeps the earliest first-seen time and fills gaps: a fact already on record is
            // never overwritten by a later observation that lacks it.
            sighting.LastSeenAt = plate.LastSeenAt;
            sighting.Confidence = Math.Max(sighting.Confidence, consensus.Confidence);
            sighting.ObservationCount += consensus.ObservationCount;
            sighting.Region = consensus.Region ?? sighting.Region;
            sighting.Latitude = location?.Latitude ?? sighting.Latitude;
            sighting.Longitude = location?.Longitude ?? sighting.Longitude;
            sighting.LocationAccuracyMeters = location?.AccuracyMeters ?? sighting.LocationAccuracyMeters;
            sighting.Make = vehicle?.Make ?? sighting.Make;
            sighting.Model = vehicle?.Model ?? sighting.Model;
            sighting.CatalogPrice = vehicle?.CatalogPrice ?? sighting.CatalogPrice;
            sighting.RegistrationYear = vehicle?.RegistrationYear ?? sighting.RegistrationYear;
            sighting.FuelDescription = vehicle?.FuelDescription ?? sighting.FuelDescription;
            sighting.BodyType = vehicle?.BodyType ?? sighting.BodyType;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToSighting(sighting);
    }

    public async Task<Sighting> ReviseAsync(
        long sightingId,
        ConfirmedPlate plate,
        VehicleRecord? vehicle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plate);
        var consensus = plate.Consensus;

        await using var context = _contexts.CreateDbContext();
        var sighting = await context.Sightings.FirstOrDefaultAsync(model => model.Id == sightingId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Sighting {sightingId} does not exist.");

        // A revision replaces the reading itself, so the corrected values win outright. Location,
        // trip and vehicle image stay as they were: only the plate was misread.
        sighting.NormalizedPlate = consensus.NormalizedPlate;
        sighting.DisplayPlate = consensus.DisplayPlate;
        sighting.Region = consensus.Region;
        sighting.LastSeenAt = plate.LastSeenAt;
        sighting.Confidence = consensus.Confidence;
        sighting.ObservationCount = consensus.ObservationCount;
        sighting.Make = vehicle?.Make;
        sighting.Model = vehicle?.Model;
        sighting.CatalogPrice = vehicle?.CatalogPrice;
        sighting.RegistrationYear = vehicle?.RegistrationYear;
        sighting.FuelDescription = vehicle?.FuelDescription;
        sighting.BodyType = vehicle?.BodyType;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToSighting(sighting);
    }

    public async Task<Sighting> SetSnapshotReferenceAsync(
        long sightingId,
        string snapshotReference,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sightingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotReference);

        await using var context = _contexts.CreateDbContext();
        var sighting = await context.Sightings.FirstOrDefaultAsync(model => model.Id == sightingId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Sighting {sightingId} does not exist.");
        sighting.SnapshotReference = snapshotReference;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToSighting(sighting);
    }

    public async Task<TripSummary> StartTripAsync(DateTimeOffset startedAt, GeoPoint? location, CancellationToken cancellationToken)
    {
        await using var context = _contexts.CreateDbContext();
        var trip = new TripModel
        {
            StartedAt = startedAt,
            StartLatitude = location?.Latitude,
            StartLongitude = location?.Longitude,
            StartAccuracyMeters = location?.AccuracyMeters
        };
        context.Trips.Add(trip);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ReadTripAsync(context, trip.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The newly created trip could not be read.");
    }

    public async Task<TripSummary> EndTripAsync(long tripId, DateTimeOffset endedAt, GeoPoint? location, CancellationToken cancellationToken)
    {
        await using var context = _contexts.CreateDbContext();
        var trip = await context.Trips.FirstOrDefaultAsync(model => model.Id == tripId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Trip {tripId} does not exist.");
        trip.EndedAt = endedAt;
        trip.EndLatitude = location?.Latitude ?? trip.EndLatitude;
        trip.EndLongitude = location?.Longitude ?? trip.EndLongitude;
        trip.EndAccuracyMeters = location?.AccuracyMeters ?? trip.EndAccuracyMeters;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ReadTripAsync(context, tripId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The ended trip could not be read.");
    }

    public async Task AddTripPointAsync(long tripId, DateTimeOffset recordedAt, GeoPoint location, CancellationToken cancellationToken)
    {
        await using var context = _contexts.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var previous = await context.TripPoints
            .Where(point => point.TripId == tripId)
            .OrderByDescending(point => point.RecordedAt)
            .Select(point => new { point.Latitude, point.Longitude })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        context.TripPoints.Add(new TripPointModel
        {
            TripId = tripId,
            RecordedAt = recordedAt,
            Latitude = location.Latitude,
            Longitude = location.Longitude,
            AccuracyMeters = location.AccuracyMeters
        });

        if (previous is not null)
        {
            var trip = await context.Trips.FirstOrDefaultAsync(model => model.Id == tripId, cancellationToken).ConfigureAwait(false);
            if (trip is not null)
            {
                trip.DistanceMeters += DistanceMeters(previous.Latitude, previous.Longitude, location.Latitude, location.Longitude);
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TripSummary>> GetTripsAsync(int offset, int limit, CancellationToken cancellationToken)
    {
        await using var context = _contexts.CreateDbContext();
        return await ReadTripsAsync(
            context.Trips.AsNoTracking()
                .OrderByDescending(trip => trip.StartedAt)
                .ThenByDescending(trip => trip.Id)
                .Skip(Math.Max(0, offset))
                .Take(Math.Clamp(limit, 1, MaximumPageSize)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TripSummary?> GetTripAsync(long tripId, CancellationToken cancellationToken)
    {
        await using var context = _contexts.CreateDbContext();
        return await ReadTripAsync(context, tripId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Sighting>> GetSightingsForTripAsync(long tripId, CancellationToken cancellationToken)
    {
        await using var context = _contexts.CreateDbContext();
        return await ReadSightingsAsync(
            context.Sightings.AsNoTracking()
                .Where(sighting => sighting.TripId == tripId)
                .OrderByDescending(sighting => sighting.LastSeenAt),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TripVehicleSummary>> GetVehiclesForTripAsync(long tripId, CancellationToken cancellationToken)
    {
        await using var context = _contexts.CreateDbContext();
        var startedAt = await context.Trips.AsNoTracking()
            .Where(trip => trip.Id == tripId)
            .Select(trip => (DateTimeOffset?)trip.StartedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (startedAt is null)
        {
            return [];
        }

        var tripStartedAt = startedAt.Value;
        var groups = await context.Sightings.AsNoTracking()
            .Where(sighting => sighting.TripId == tripId)
            .GroupBy(sighting => sighting.NormalizedPlate)
            .Select(group => new
            {
                NormalizedPlate = group.Key,
                DisplayPlate = group.Max(sighting => sighting.DisplayPlate),
                FirstSeenAt = group.Min(sighting => sighting.FirstSeenAt),
                LastSeenAt = group.Max(sighting => sighting.LastSeenAt),
                Confidence = group.Max(sighting => sighting.Confidence),
                ObservationCount = group.Sum(sighting => sighting.ObservationCount),
                SightingCount = group.Count(),
                Make = group.Max(sighting => sighting.Make),
                Model = group.Max(sighting => sighting.Model),
                CatalogPrice = group.Max(sighting => sighting.CatalogPrice),
                RegistrationYear = group.Max(sighting => sighting.RegistrationYear),
                FuelDescription = group.Max(sighting => sighting.FuelDescription),
                BodyType = group.Max(sighting => sighting.BodyType)
            })
            .OrderBy(group => group.FirstSeenAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (groups.Count == 0)
        {
            return [];
        }

        var plates = groups.ConvertAll(group => group.NormalizedPlate);
        var earlierCounts = (await context.Sightings.AsNoTracking()
            .Where(sighting => plates.Contains(sighting.NormalizedPlate) && sighting.LastSeenAt < tripStartedAt)
            .GroupBy(sighting => sighting.NormalizedPlate)
            .Select(group => new { NormalizedPlate = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(row => row.NormalizedPlate, row => row.Count, StringComparer.Ordinal);

        var locations = await ReadLatestLocationsAsync(
            context.Sightings.AsNoTracking().Where(sighting => sighting.TripId == tripId),
            cancellationToken).ConfigureAwait(false);
        var snapshots = await ReadLatestSnapshotsAsync(
            context.Sightings.AsNoTracking().Where(sighting => sighting.TripId == tripId),
            cancellationToken).ConfigureAwait(false);

        return groups.ConvertAll(group => new TripVehicleSummary(
            group.NormalizedPlate,
            group.DisplayPlate!,
            group.FirstSeenAt,
            group.LastSeenAt,
            group.Confidence,
            group.ObservationCount,
            group.SightingCount,
            earlierCounts.GetValueOrDefault(group.NormalizedPlate),
            ToVehicle(group.NormalizedPlate, group.Make, group.Model, group.CatalogPrice, group.RegistrationYear, group.FuelDescription, group.BodyType),
            locations.GetValueOrDefault(group.NormalizedPlate),
            snapshots.GetValueOrDefault(group.NormalizedPlate)));
    }

    public async Task<IReadOnlyList<TripPoint>> GetTripPointsAsync(long tripId, CancellationToken cancellationToken)
    {
        await using var context = _contexts.CreateDbContext();
        var rows = await context.TripPoints.AsNoTracking()
            .Where(point => point.TripId == tripId)
            .OrderBy(point => point.RecordedAt)
            .Select(point => new { point.Id, point.TripId, point.RecordedAt, point.Latitude, point.Longitude, point.AccuracyMeters })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ConvertAll(row => new TripPoint(
            row.Id,
            row.TripId,
            row.RecordedAt,
            new GeoPoint(row.Latitude, row.Longitude, row.AccuracyMeters)));
    }

    public async Task<IReadOnlyList<VehicleHistorySummary>> GetVehicleHistoryAsync(VehicleHistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var context = _contexts.CreateDbContext();
        var sightings = context.Sightings.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var platePattern = $"%{PlateText.Normalize(query.Search)}%";
            var textPattern = $"%{query.Search.Trim()}%";
            sightings = sightings.Where(sighting =>
                EF.Functions.Like(sighting.NormalizedPlate, platePattern)
                || (sighting.Make != null && EF.Functions.Like(sighting.Make, textPattern))
                || (sighting.Model != null && EF.Functions.Like(sighting.Model, textPattern)));
        }

        var grouped = sightings.GroupBy(sighting => sighting.NormalizedPlate);
        if (query.SeenSince is not null)
        {
            var seenSince = query.SeenSince.Value;
            grouped = grouped.Where(group => group.Max(sighting => sighting.LastSeenAt) >= seenSince);
        }

        if (query.MinimumCatalogPrice is not null)
        {
            var minimumPrice = query.MinimumCatalogPrice.Value;
            grouped = grouped.Where(group => group.Max(sighting => sighting.CatalogPrice) > minimumPrice);
        }

        var projected = grouped.Select(group => new
        {
            NormalizedPlate = group.Key,
            DisplayPlate = group.Max(sighting => sighting.DisplayPlate),
            FirstSeenAt = group.Min(sighting => sighting.FirstSeenAt),
            LastSeenAt = group.Max(sighting => sighting.LastSeenAt),
            SightingCount = group.Count(),
            TripCount = group.Select(sighting => sighting.TripId).Distinct().Count(),
            Make = group.Max(sighting => sighting.Make),
            Model = group.Max(sighting => sighting.Model),
            CatalogPrice = group.Max(sighting => sighting.CatalogPrice),
            RegistrationYear = group.Max(sighting => sighting.RegistrationYear),
            FuelDescription = group.Max(sighting => sighting.FuelDescription),
            BodyType = group.Max(sighting => sighting.BodyType)
        });

        var ordered = query.Sort == VehicleHistorySort.HighestValue
            ? projected
                .OrderByDescending(row => row.CatalogPrice)
                .ThenByDescending(row => row.LastSeenAt)
                .ThenBy(row => row.NormalizedPlate)
            : projected
                .OrderByDescending(row => row.LastSeenAt)
                .ThenBy(row => row.NormalizedPlate);

        var rows = await ordered
            .Skip(Math.Max(0, query.Offset))
            .Take(Math.Clamp(query.Limit, 1, MaximumPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return [];
        }

        var plates = rows.ConvertAll(row => row.NormalizedPlate);
        var candidates = context.Sightings.AsNoTracking().Where(sighting => plates.Contains(sighting.NormalizedPlate));
        var locations = await ReadLatestLocationsAsync(candidates, cancellationToken).ConfigureAwait(false);
        var snapshots = await ReadLatestSnapshotsAsync(candidates, cancellationToken).ConfigureAwait(false);

        return rows.ConvertAll(row => new VehicleHistorySummary(
            row.NormalizedPlate,
            row.DisplayPlate!,
            row.FirstSeenAt,
            row.LastSeenAt,
            row.SightingCount,
            row.TripCount,
            ToVehicle(row.NormalizedPlate, row.Make, row.Model, row.CatalogPrice, row.RegistrationYear, row.FuelDescription, row.BodyType),
            locations.GetValueOrDefault(row.NormalizedPlate),
            snapshots.GetValueOrDefault(row.NormalizedPlate)));
    }

    public async Task<HistoryStatistics> GetStatisticsAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken)
    {
        await using var context = _contexts.CreateDbContext();
        var trips = context.Trips.AsNoTracking()
            .Where(trip => trip.StartedAt < until && (trip.EndedAt ?? until) >= from);
        var sightings = context.Sightings.AsNoTracking()
            .Where(sighting => sighting.LastSeenAt >= from && sighting.LastSeenAt < until);

        var tripCount = await trips.CountAsync(cancellationToken).ConfigureAwait(false);
        var distanceMeters = await trips.SumAsync(trip => trip.DistanceMeters, cancellationToken).ConfigureAwait(false);
        var sightingCount = await sightings.CountAsync(cancellationToken).ConfigureAwait(false);
        var uniqueVehicleCount = await sightings
            .Select(sighting => sighting.NormalizedPlate)
            .Distinct()
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
        var mostExpensive = await ReadSightingsAsync(
            sightings
                .Where(sighting => sighting.CatalogPrice != null)
                .OrderByDescending(sighting => sighting.CatalogPrice)
                .ThenByDescending(sighting => sighting.LastSeenAt)
                .Take(1),
            cancellationToken).ConfigureAwait(false);
        return new HistoryStatistics(tripCount, sightingCount, uniqueVehicleCount, distanceMeters, mostExpensive.FirstOrDefault());
    }

    public async Task<IReadOnlyList<Sighting>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        await using var context = _contexts.CreateDbContext();
        return await ReadSightingsAsync(
            context.Sightings.AsNoTracking()
                .OrderByDescending(sighting => sighting.LastSeenAt)
                .Take(Math.Clamp(limit, 1, MaximumPageSize)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Sighting>> GetAllSightingsAsync(CancellationToken cancellationToken)
    {
        await using var context = _contexts.CreateDbContext();
        return await ReadSightingsAsync(
            context.Sightings.AsNoTracking().OrderByDescending(sighting => sighting.LastSeenAt),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Sighting>> FindByPlateAsync(string normalizedPlate, CancellationToken cancellationToken)
    {
        var plate = PlateText.Normalize(normalizedPlate);
        await using var context = _contexts.CreateDbContext();
        return await ReadSightingsAsync(
            context.Sightings.AsNoTracking()
                .Where(sighting => sighting.NormalizedPlate == plate)
                .OrderByDescending(sighting => sighting.LastSeenAt),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Sighting?> GetMostExpensiveAsync(CancellationToken cancellationToken)
    {
        await using var context = _contexts.CreateDbContext();
        var sightings = await ReadSightingsAsync(
            context.Sightings.AsNoTracking()
                .Where(sighting => sighting.CatalogPrice != null)
                .OrderByDescending(sighting => sighting.CatalogPrice)
                .ThenByDescending(sighting => sighting.LastSeenAt)
                .Take(1),
            cancellationToken).ConfigureAwait(false);
        return sightings.FirstOrDefault();
    }

    public async Task DeleteHistoryAsync(CancellationToken cancellationToken)
    {
        await using var context = _contexts.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await context.TripPoints.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.Sightings.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.Trips.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reduces a candidate set to one row per plate: the most recent one, with the row identifier
    /// breaking ties. Expressed as "no other candidate is newer" so SQLite runs a single NOT EXISTS.
    /// </summary>
    private static IQueryable<SightingModel> LatestPerPlate(IQueryable<SightingModel> candidates) =>
        candidates.Where(sighting => !candidates.Any(other =>
            other.NormalizedPlate == sighting.NormalizedPlate
            && (other.LastSeenAt > sighting.LastSeenAt
                || (other.LastSeenAt == sighting.LastSeenAt && other.Id > sighting.Id))));

    // The value is nullable so a plate without any located sighting reads back as null rather than
    // as the default GeoPoint, which would put it off the coast of Africa.
    private static async Task<Dictionary<string, GeoPoint?>> ReadLatestLocationsAsync(
        IQueryable<SightingModel> candidates,
        CancellationToken cancellationToken)
    {
        var rows = await LatestPerPlate(candidates.Where(sighting => sighting.Latitude != null && sighting.Longitude != null))
            .Select(sighting => new { sighting.NormalizedPlate, sighting.Latitude, sighting.Longitude, sighting.LocationAccuracyMeters })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(
            row => row.NormalizedPlate,
            row => (GeoPoint?)new GeoPoint(row.Latitude!.Value, row.Longitude!.Value, row.LocationAccuracyMeters),
            StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, string>> ReadLatestSnapshotsAsync(
        IQueryable<SightingModel> candidates,
        CancellationToken cancellationToken)
    {
        var rows = await LatestPerPlate(candidates.Where(sighting => sighting.SnapshotReference != null))
            .Select(sighting => new { sighting.NormalizedPlate, sighting.SnapshotReference })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(row => row.NormalizedPlate, row => row.SnapshotReference!, StringComparer.Ordinal);
    }

    private static async Task<TripSummary?> ReadTripAsync(LprDbContext context, long tripId, CancellationToken cancellationToken)
    {
        var trips = await ReadTripsAsync(
            context.Trips.AsNoTracking().Where(trip => trip.Id == tripId),
            cancellationToken).ConfigureAwait(false);
        return trips.Count == 0 ? null : trips[0];
    }

    private static async Task<IReadOnlyList<TripSummary>> ReadTripsAsync(IQueryable<TripModel> trips, CancellationToken cancellationToken)
    {
        var rows = await trips
            .Select(trip => new
            {
                trip.Id,
                trip.StartedAt,
                trip.EndedAt,
                trip.DistanceMeters,
                SightingCount = trip.Sightings.Count(),
                UniqueVehicleCount = trip.Sightings.Select(sighting => sighting.NormalizedPlate).Distinct().Count(),
                MostExpensiveCatalogPrice = trip.Sightings
                    .Where(sighting => sighting.CatalogPrice != null)
                    .OrderByDescending(sighting => sighting.CatalogPrice)
                    .Select(sighting => sighting.CatalogPrice)
                    .FirstOrDefault(),
                MostExpensiveDisplayPlate = trip.Sightings
                    .Where(sighting => sighting.CatalogPrice != null)
                    .OrderByDescending(sighting => sighting.CatalogPrice)
                    .Select(sighting => sighting.DisplayPlate)
                    .FirstOrDefault(),
                trip.StartLatitude,
                trip.StartLongitude,
                trip.StartAccuracyMeters,
                trip.EndLatitude,
                trip.EndLongitude,
                trip.EndAccuracyMeters
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ConvertAll(row => new TripSummary(
            row.Id,
            row.StartedAt,
            row.EndedAt,
            row.DistanceMeters,
            row.SightingCount,
            row.UniqueVehicleCount,
            row.MostExpensiveCatalogPrice,
            row.MostExpensiveDisplayPlate,
            ToLocation(row.StartLatitude, row.StartLongitude, row.StartAccuracyMeters),
            ToLocation(row.EndLatitude, row.EndLongitude, row.EndAccuracyMeters)));
    }

    private static async Task<IReadOnlyList<Sighting>> ReadSightingsAsync(IQueryable<SightingModel> sightings, CancellationToken cancellationToken)
    {
        var rows = await sightings.ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ConvertAll(ToSighting);
    }

    private static async Task<bool> IsPreEfCoreDatabaseAsync(LprDbContext context, CancellationToken cancellationToken)
    {
        var tables = await context.Database
            .SqlQuery<string>($"SELECT name AS \"Value\" FROM sqlite_master WHERE type = 'table'")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return tables.Contains("sightings", StringComparer.Ordinal)
            && !tables.Contains("__EFMigrationsHistory", StringComparer.Ordinal);
    }

    private static async Task CloseInterruptedTripsAsync(LprDbContext context, CancellationToken cancellationToken)
    {
        var openTrips = await context.Trips
            .Where(trip => trip.EndedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (openTrips.Count == 0)
        {
            return;
        }

        // Recover a drive that process termination cut short, at its last useful timestamp.
        foreach (var trip in openTrips)
        {
            var lastPoint = await context.TripPoints
                .Where(point => point.TripId == trip.Id)
                .OrderByDescending(point => point.RecordedAt)
                .Select(point => (DateTimeOffset?)point.RecordedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            var lastSighting = await context.Sightings
                .Where(sighting => sighting.TripId == trip.Id)
                .OrderByDescending(sighting => sighting.LastSeenAt)
                .Select(sighting => (DateTimeOffset?)sighting.LastSeenAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            trip.EndedAt = lastPoint ?? lastSighting ?? trip.StartedAt;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Sighting ToSighting(SightingModel model) => new(
        model.Id,
        model.NormalizedPlate,
        model.DisplayPlate,
        model.Region,
        model.FirstSeenAt,
        model.LastSeenAt,
        model.Confidence,
        model.ObservationCount,
        ToLocation(model.Latitude, model.Longitude, model.LocationAccuracyMeters),
        ToVehicle(
            model.NormalizedPlate,
            model.Make,
            model.Model,
            model.CatalogPrice,
            model.RegistrationYear,
            model.FuelDescription,
            model.BodyType))
    {
        TripId = model.TripId,
        SnapshotReference = model.SnapshotReference
    };

    private static VehicleRecord? ToVehicle(
        string normalizedPlate,
        string? make,
        string? model,
        decimal? catalogPrice,
        int? registrationYear,
        string? fuelDescription,
        string? bodyType) =>
        make is null && model is null && catalogPrice is null
            ? null
            : new VehicleRecord(normalizedPlate, make, model, catalogPrice, registrationYear, fuelDescription, bodyType);

    private static GeoPoint? ToLocation(double? latitude, double? longitude, float? accuracyMeters) =>
        latitude is null || longitude is null ? null : new GeoPoint(latitude.Value, longitude.Value, accuracyMeters);

    private static double DistanceMeters(double fromLatitude, double fromLongitude, double toLatitude, double toLongitude)
    {
        const double earthRadius = 6_371_000;
        var latitudeDelta = DegreesToRadians(toLatitude - fromLatitude);
        var longitudeDelta = DegreesToRadians(toLongitude - fromLongitude);
        var a = (Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2))
            + (Math.Cos(DegreesToRadians(fromLatitude)) * Math.Cos(DegreesToRadians(toLatitude))
                * Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2));
        return earthRadius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180;
}
