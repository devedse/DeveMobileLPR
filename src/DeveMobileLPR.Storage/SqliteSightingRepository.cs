using System.Globalization;
using DeveMobileLPR.Recognition;
using Microsoft.Data.Sqlite;

namespace DeveMobileLPR.Storage;

public sealed class SqliteSightingRepository : ISightingRepository
{
    private readonly SqliteConnectionFactory _connections;
    private readonly TimeSpan _mergeWindow;

    public SqliteSightingRepository(string databasePath, TimeSpan? mergeWindow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connections = new SqliteConnectionFactory(databasePath);
        _mergeWindow = mergeWindow ?? TimeSpan.FromMinutes(3);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, Schema, cancellationToken).ConfigureAwait(false);
        if (!await HasColumnAsync(connection, "sightings", "trip_id", cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync(connection, "ALTER TABLE sightings ADD COLUMN trip_id INTEGER NULL REFERENCES trips(id) ON DELETE SET NULL;", cancellationToken).ConfigureAwait(false);
        }

        if (!await HasColumnAsync(connection, "sightings", "snapshot_reference", cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync(connection, "ALTER TABLE sightings ADD COLUMN snapshot_reference TEXT NULL;", cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, Indexes, cancellationToken).ConfigureAwait(false);
        // Recover a drive interrupted by process termination at its last useful timestamp.
        await ExecuteAsync(connection, """
            UPDATE trips
            SET ended_at = COALESCE(
                (SELECT MAX(recorded_at) FROM trip_points WHERE trip_id = trips.id),
                (SELECT MAX(last_seen_at) FROM sightings WHERE trip_id = trips.id),
                started_at)
            WHERE ended_at IS NULL;
                PRAGMA user_version = 3;
            """, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Sighting> AddOrMergeAsync(
        ConfirmedPlate plate,
        GeoPoint? location,
        VehicleRecord? vehicle,
        long? tripId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plate);
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existingId = await FindMergeCandidateAsync(
            connection,
            transaction,
            plate.Consensus.NormalizedPlate,
            plate.FirstSeenAt - _mergeWindow,
            tripId,
            cancellationToken).ConfigureAwait(false);

        long id;
        if (existingId is not null)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE sightings SET
                    last_seen_at = @last_seen_at,
                    confidence = MAX(confidence, @confidence),
                    observation_count = observation_count + @observation_count,
                    latitude = COALESCE(@latitude, latitude),
                    longitude = COALESCE(@longitude, longitude),
                    location_accuracy_meters = COALESCE(@accuracy, location_accuracy_meters),
                    region = COALESCE(@region, region),
                    make = COALESCE(@make, make),
                    model = COALESCE(@model, model),
                    catalog_price = COALESCE(@catalog_price, catalog_price),
                    registration_year = COALESCE(@registration_year, registration_year),
                    fuel_description = COALESCE(@fuel, fuel_description),
                    body_type = COALESCE(@body_type, body_type)
                WHERE id = @id;
                """;
            AddCommonParameters(update, plate, location, vehicle, tripId);
            update.Parameters.AddWithValue("@id", existingId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            id = existingId.Value;
        }
        else
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO sightings (
                    normalized_plate, display_plate, region, first_seen_at, last_seen_at,
                    confidence, observation_count, latitude, longitude, location_accuracy_meters,
                    make, model, catalog_price, registration_year, fuel_description, body_type, trip_id)
                VALUES (
                    @normalized_plate, @display_plate, @region, @first_seen_at, @last_seen_at,
                    @confidence, @observation_count, @latitude, @longitude, @accuracy,
                    @make, @model, @catalog_price, @registration_year, @fuel, @body_type, @trip_id);
                SELECT last_insert_rowid();
                """;
            AddCommonParameters(insert, plate, location, vehicle, tripId);
            id = (long)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("SQLite did not return a sighting identifier."));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Sighting> SetSnapshotReferenceAsync(
        long sightingId,
        string snapshotReference,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sightingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotReference);
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sightings SET snapshot_reference = @reference WHERE id = @id;";
        command.Parameters.AddWithValue("@reference", snapshotReference);
        command.Parameters.AddWithValue("@id", sightingId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException($"Sighting {sightingId} does not exist.");
        }

        return await GetByIdAsync(sightingId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TripSummary> StartTripAsync(DateTimeOffset startedAt, GeoPoint? location, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trips (started_at, start_latitude, start_longitude, start_accuracy_meters)
            VALUES (@started_at, @latitude, @longitude, @accuracy);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@started_at", Timestamp(startedAt));
        AddLocationParameters(command, location);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return await GetTripAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The newly created trip could not be read.");
    }

    public async Task<TripSummary> EndTripAsync(long tripId, DateTimeOffset endedAt, GeoPoint? location, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE trips SET
                ended_at = @ended_at,
                end_latitude = COALESCE(@latitude, end_latitude),
                end_longitude = COALESCE(@longitude, end_longitude),
                end_accuracy_meters = COALESCE(@accuracy, end_accuracy_meters)
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", tripId);
        command.Parameters.AddWithValue("@ended_at", Timestamp(endedAt));
        AddLocationParameters(command, location);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException($"Trip {tripId} does not exist.");
        }

        return await GetTripAsync(tripId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The ended trip could not be read.");
    }

    public async Task AddTripPointAsync(long tripId, DateTimeOffset recordedAt, GeoPoint location, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        GeoPoint? previous = null;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = (SqliteTransaction)transaction;
            read.CommandText = "SELECT latitude, longitude, accuracy_meters FROM trip_points WHERE trip_id = @trip_id ORDER BY recorded_at DESC LIMIT 1;";
            read.Parameters.AddWithValue("@trip_id", tripId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                previous = new GeoPoint(reader.GetDouble(0), reader.GetDouble(1), reader.IsDBNull(2) ? null : reader.GetFloat(2));
            }
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO trip_points (trip_id, recorded_at, latitude, longitude, accuracy_meters) VALUES (@trip_id, @recorded_at, @latitude, @longitude, @accuracy);";
            insert.Parameters.AddWithValue("@trip_id", tripId);
            insert.Parameters.AddWithValue("@recorded_at", Timestamp(recordedAt));
            insert.Parameters.AddWithValue("@latitude", location.Latitude);
            insert.Parameters.AddWithValue("@longitude", location.Longitude);
            insert.Parameters.AddWithValue("@accuracy", DbValue(location.AccuracyMeters));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (previous is not null)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = "UPDATE trips SET distance_meters = distance_meters + @distance WHERE id = @trip_id;";
            update.Parameters.AddWithValue("@trip_id", tripId);
            update.Parameters.AddWithValue("@distance", DistanceMeters(previous.Value, location));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<TripSummary>> GetTripsAsync(int offset, int limit, CancellationToken cancellationToken) =>
        QueryTripsAsync("ORDER BY t.started_at DESC, t.id DESC LIMIT @limit OFFSET @offset", command =>
        {
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));
            command.Parameters.AddWithValue("@offset", Math.Max(0, offset));
        }, cancellationToken);

    public async Task<TripSummary?> GetTripAsync(long tripId, CancellationToken cancellationToken)
    {
        var rows = await QueryTripsAsync("WHERE t.id = @id", command => command.Parameters.AddWithValue("@id", tripId), cancellationToken).ConfigureAwait(false);
        return rows.SingleOrDefault();
    }

    public Task<IReadOnlyList<Sighting>> GetSightingsForTripAsync(long tripId, CancellationToken cancellationToken) =>
        QuerySightingsAsync("SELECT * FROM sightings WHERE trip_id = @trip_id ORDER BY last_seen_at DESC;", command => command.Parameters.AddWithValue("@trip_id", tripId), cancellationToken);

    public async Task<IReadOnlyList<TripVehicleSummary>> GetVehiclesForTripAsync(long tripId, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                s.normalized_plate,
                MAX(s.display_plate) AS display_plate,
                MIN(s.first_seen_at) AS first_seen_at,
                MAX(s.last_seen_at) AS last_seen_at,
                MAX(s.confidence) AS confidence,
                SUM(s.observation_count) AS observation_count,
                COUNT(*) AS sighting_count,
                (
                    SELECT COUNT(*)
                    FROM sightings earlier
                    WHERE earlier.normalized_plate = s.normalized_plate
                      AND earlier.last_seen_at < (SELECT started_at FROM trips WHERE id = @trip_id)
                ) AS earlier_sighting_count,
                MAX(s.make) AS make,
                MAX(s.model) AS model,
                MAX(s.catalog_price) AS catalog_price,
                MAX(s.registration_year) AS registration_year,
                MAX(s.fuel_description) AS fuel_description,
                MAX(s.body_type) AS body_type,
                (
                    SELECT latitude
                    FROM sightings latest
                    WHERE latest.trip_id = @trip_id
                      AND latest.normalized_plate = s.normalized_plate
                      AND latest.latitude IS NOT NULL
                    ORDER BY latest.last_seen_at DESC
                    LIMIT 1
                ) AS latitude,
                (
                    SELECT longitude
                    FROM sightings latest
                    WHERE latest.trip_id = @trip_id
                      AND latest.normalized_plate = s.normalized_plate
                      AND latest.latitude IS NOT NULL
                    ORDER BY latest.last_seen_at DESC
                    LIMIT 1
                ) AS longitude,
                (
                    SELECT location_accuracy_meters
                    FROM sightings latest
                    WHERE latest.trip_id = @trip_id
                      AND latest.normalized_plate = s.normalized_plate
                      AND latest.latitude IS NOT NULL
                    ORDER BY latest.last_seen_at DESC
                    LIMIT 1
                ) AS accuracy
            FROM sightings s
            WHERE s.trip_id = @trip_id
            GROUP BY s.normalized_plate
            ORDER BY first_seen_at;
            """;
        command.Parameters.AddWithValue("@trip_id", tripId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<TripVehicleSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var plate = reader.GetString(0);
            var vehicle = reader.IsDBNull(8) && reader.IsDBNull(9) && reader.IsDBNull(10)
                ? null
                : new VehicleRecord(
                    plate,
                    GetNullableString(reader, 8),
                    GetNullableString(reader, 9),
                    GetNullableDecimal(reader, 10),
                    GetNullableInt32(reader, 11),
                    GetNullableString(reader, 12),
                    GetNullableString(reader, 13));
            results.Add(new TripVehicleSummary(
                plate,
                reader.GetString(1),
                ParseTimestamp(reader.GetString(2)),
                ParseTimestamp(reader.GetString(3)),
                reader.GetFloat(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                vehicle,
                ReadLocation(reader, 14, 15, 16)));
        }

        return results;
    }

    public async Task<IReadOnlyList<TripPoint>> GetTripPointsAsync(long tripId, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, trip_id, recorded_at, latitude, longitude, accuracy_meters FROM trip_points WHERE trip_id = @trip_id ORDER BY recorded_at;";
        command.Parameters.AddWithValue("@trip_id", tripId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<TripPoint>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new TripPoint(
                reader.GetInt64(0),
                reader.GetInt64(1),
                ParseTimestamp(reader.GetString(2)),
                new GeoPoint(reader.GetDouble(3), reader.GetDouble(4), reader.IsDBNull(5) ? null : reader.GetFloat(5))));
        }

        return results;
    }

    public async Task<IReadOnlyList<VehicleHistorySummary>> GetVehicleHistoryAsync(VehicleHistoryQuery query, CancellationToken cancellationToken)
    {
        var normalizedSearch = string.IsNullOrWhiteSpace(query.Search) ? null : PlateText.Normalize(query.Search);
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                normalized_plate,
                MAX(display_plate) AS display_plate,
                MIN(first_seen_at) AS first_seen_at,
                MAX(last_seen_at) AS last_seen_at,
                COUNT(*) AS sighting_count,
                COUNT(DISTINCT trip_id) AS trip_count,
                MAX(make) AS make,
                MAX(model) AS model,
                MAX(catalog_price) AS catalog_price,
                MAX(registration_year) AS registration_year,
                MAX(fuel_description) AS fuel_description,
                MAX(body_type) AS body_type,
                (SELECT latitude FROM sightings latest WHERE latest.normalized_plate = sightings.normalized_plate AND latitude IS NOT NULL ORDER BY last_seen_at DESC LIMIT 1) AS latitude,
                (SELECT longitude FROM sightings latest WHERE latest.normalized_plate = sightings.normalized_plate AND longitude IS NOT NULL ORDER BY last_seen_at DESC LIMIT 1) AS longitude,
                (SELECT location_accuracy_meters FROM sightings latest WHERE latest.normalized_plate = sightings.normalized_plate AND latitude IS NOT NULL ORDER BY last_seen_at DESC LIMIT 1) AS accuracy
            FROM sightings
            WHERE @search IS NULL OR normalized_plate LIKE '%' || @search || '%' OR make LIKE '%' || @raw_search || '%' OR model LIKE '%' || @raw_search || '%'
            GROUP BY normalized_plate
            HAVING (@seen_since IS NULL OR MAX(last_seen_at) >= @seen_since)
                AND (@minimum_price IS NULL OR MAX(catalog_price) > @minimum_price)
            ORDER BY
                CASE WHEN @sort = 1 THEN MAX(catalog_price) END DESC,
                MAX(last_seen_at) DESC,
                normalized_plate
            LIMIT @limit OFFSET @offset;
            """;
        command.Parameters.AddWithValue("@search", DbValue(normalizedSearch));
        command.Parameters.AddWithValue("@raw_search", DbValue(string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim()));
        command.Parameters.AddWithValue("@seen_since", DbValue(query.SeenSince is null ? null : Timestamp(query.SeenSince.Value)));
        command.Parameters.AddWithValue("@minimum_price", query.MinimumCatalogPrice is null ? DBNull.Value : decimal.ToDouble(query.MinimumCatalogPrice.Value));
        command.Parameters.AddWithValue("@sort", (int)query.Sort);
        command.Parameters.AddWithValue("@limit", Math.Clamp(query.Limit, 1, 1000));
        command.Parameters.AddWithValue("@offset", Math.Max(0, query.Offset));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<VehicleHistorySummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var plate = reader.GetString(0);
            var vehicle = reader.IsDBNull(6) && reader.IsDBNull(7) && reader.IsDBNull(8)
                ? null
                : new VehicleRecord(plate, GetNullableString(reader, 6), GetNullableString(reader, 7), GetNullableDecimal(reader, 8), GetNullableInt32(reader, 9), GetNullableString(reader, 10), GetNullableString(reader, 11));
            GeoPoint? location = reader.IsDBNull(12) || reader.IsDBNull(13)
                ? null
                : new GeoPoint(reader.GetDouble(12), reader.GetDouble(13), reader.IsDBNull(14) ? null : reader.GetFloat(14));
            results.Add(new VehicleHistorySummary(
                plate,
                reader.GetString(1),
                ParseTimestamp(reader.GetString(2)),
                ParseTimestamp(reader.GetString(3)),
                reader.GetInt32(4),
                reader.GetInt32(5),
                vehicle,
                location));
        }

        return results;
    }

    public async Task<HistoryStatistics> GetStatisticsAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken)
    {
        int tripCount;
        int sightingCount;
        int uniqueCount;
        double distance;
        await using (var connection = _connections.Create())
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM trips WHERE started_at < @until AND COALESCE(ended_at, @until) >= @from),
                    (SELECT COUNT(*) FROM sightings WHERE last_seen_at >= @from AND last_seen_at < @until),
                    (SELECT COUNT(DISTINCT normalized_plate) FROM sightings WHERE last_seen_at >= @from AND last_seen_at < @until),
                    COALESCE((SELECT SUM(distance_meters) FROM trips WHERE started_at < @until AND COALESCE(ended_at, @until) >= @from), 0);
                """;
            command.Parameters.AddWithValue("@from", Timestamp(from));
            command.Parameters.AddWithValue("@until", Timestamp(until));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            tripCount = reader.GetInt32(0);
            sightingCount = reader.GetInt32(1);
            uniqueCount = reader.GetInt32(2);
            distance = reader.GetDouble(3);
        }

        var mostExpensive = (await QuerySightingsAsync(
            "SELECT * FROM sightings WHERE last_seen_at >= @from AND last_seen_at < @until AND catalog_price IS NOT NULL ORDER BY catalog_price DESC, last_seen_at DESC LIMIT 1;",
            c => { c.Parameters.AddWithValue("@from", Timestamp(from)); c.Parameters.AddWithValue("@until", Timestamp(until)); }, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        return new HistoryStatistics(tripCount, sightingCount, uniqueCount, distance, mostExpensive);
    }

    public Task<IReadOnlyList<Sighting>> GetRecentAsync(int limit, CancellationToken cancellationToken) =>
        QuerySightingsAsync("SELECT * FROM sightings ORDER BY last_seen_at DESC LIMIT @limit;", command => command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000)), cancellationToken);

    public Task<IReadOnlyList<Sighting>> GetAllSightingsAsync(CancellationToken cancellationToken) =>
        QuerySightingsAsync("SELECT * FROM sightings ORDER BY last_seen_at DESC;", null, cancellationToken);

    public Task<IReadOnlyList<Sighting>> FindByPlateAsync(string normalizedPlate, CancellationToken cancellationToken) =>
        QuerySightingsAsync("SELECT * FROM sightings WHERE normalized_plate = @plate ORDER BY last_seen_at DESC;", command => command.Parameters.AddWithValue("@plate", PlateText.Normalize(normalizedPlate)), cancellationToken);

    public async Task<Sighting?> GetMostExpensiveAsync(CancellationToken cancellationToken) =>
        (await QuerySightingsAsync("SELECT * FROM sightings WHERE catalog_price IS NOT NULL ORDER BY catalog_price DESC, last_seen_at DESC LIMIT 1;", null, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    public async Task DeleteHistoryAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var sql in new[] { "DELETE FROM trip_points;", "DELETE FROM sightings;", "DELETE FROM trips;" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Sighting> GetByIdAsync(long id, CancellationToken cancellationToken) =>
        (await QuerySightingsAsync("SELECT * FROM sightings WHERE id = @id;", command => command.Parameters.AddWithValue("@id", id), cancellationToken).ConfigureAwait(false)).Single();

    private async Task<IReadOnlyList<Sighting>> QuerySightingsAsync(string sql, Action<SqliteCommand>? configure, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<Sighting>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadSighting(reader));
        }

        return results;
    }

    private async Task<IReadOnlyList<TripSummary>> QueryTripsAsync(string suffix, Action<SqliteCommand>? configure, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                t.id, t.started_at, t.ended_at, t.distance_meters,
                (SELECT COUNT(*) FROM sightings s WHERE s.trip_id = t.id) AS sighting_count,
                (SELECT COUNT(DISTINCT normalized_plate) FROM sightings s WHERE s.trip_id = t.id) AS unique_count,
                (SELECT catalog_price FROM sightings s WHERE s.trip_id = t.id AND catalog_price IS NOT NULL ORDER BY catalog_price DESC LIMIT 1) AS top_price,
                (SELECT display_plate FROM sightings s WHERE s.trip_id = t.id AND catalog_price IS NOT NULL ORDER BY catalog_price DESC LIMIT 1) AS top_plate,
                t.start_latitude, t.start_longitude, t.start_accuracy_meters,
                t.end_latitude, t.end_longitude, t.end_accuracy_meters
            FROM trips t
            {suffix};
            """;
        configure?.Invoke(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<TripSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new TripSummary(
                reader.GetInt64(0),
                ParseTimestamp(reader.GetString(1)),
                reader.IsDBNull(2) ? null : ParseTimestamp(reader.GetString(2)),
                reader.GetDouble(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                GetNullableDecimal(reader, 6),
                GetNullableString(reader, 7),
                ReadLocation(reader, 8, 9, 10),
                ReadLocation(reader, 11, 12, 13)));
        }

        return results;
    }

    private static async Task<long?> FindMergeCandidateAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string normalizedPlate, DateTimeOffset cutoff, long? tripId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            SELECT id FROM sightings
            WHERE normalized_plate = @plate
              AND last_seen_at >= @cutoff
              AND (trip_id = @trip_id OR (trip_id IS NULL AND @trip_id IS NULL))
            ORDER BY last_seen_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("@plate", normalizedPlate);
        command.Parameters.AddWithValue("@cutoff", Timestamp(cutoff));
        command.Parameters.AddWithValue("@trip_id", DbValue(tripId));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static void AddCommonParameters(SqliteCommand command, ConfirmedPlate plate, GeoPoint? location, VehicleRecord? vehicle, long? tripId)
    {
        command.Parameters.AddWithValue("@normalized_plate", plate.Consensus.NormalizedPlate);
        command.Parameters.AddWithValue("@display_plate", plate.Consensus.DisplayPlate);
        command.Parameters.AddWithValue("@region", DbValue(plate.Consensus.Region));
        command.Parameters.AddWithValue("@first_seen_at", Timestamp(plate.FirstSeenAt));
        command.Parameters.AddWithValue("@last_seen_at", Timestamp(plate.LastSeenAt));
        command.Parameters.AddWithValue("@confidence", plate.Consensus.Confidence);
        command.Parameters.AddWithValue("@observation_count", plate.Consensus.ObservationCount);
        AddLocationParameters(command, location);
        command.Parameters.AddWithValue("@make", DbValue(vehicle?.Make));
        command.Parameters.AddWithValue("@model", DbValue(vehicle?.Model));
        command.Parameters.AddWithValue("@catalog_price", DbValue(vehicle?.CatalogPrice));
        command.Parameters.AddWithValue("@registration_year", DbValue(vehicle?.RegistrationYear));
        command.Parameters.AddWithValue("@fuel", DbValue(vehicle?.FuelDescription));
        command.Parameters.AddWithValue("@body_type", DbValue(vehicle?.BodyType));
        command.Parameters.AddWithValue("@trip_id", DbValue(tripId));
    }

    private static void AddLocationParameters(SqliteCommand command, GeoPoint? location)
    {
        command.Parameters.AddWithValue("@latitude", DbValue(location?.Latitude));
        command.Parameters.AddWithValue("@longitude", DbValue(location?.Longitude));
        command.Parameters.AddWithValue("@accuracy", DbValue(location?.AccuracyMeters));
    }

    private static Sighting ReadSighting(SqliteDataReader reader)
    {
        var location = ReadLocation(reader, "latitude", "longitude", "location_accuracy_meters");
        var plate = reader.GetString(reader.GetOrdinal("normalized_plate"));
        var vehicle = HasVehicle(reader)
            ? new VehicleRecord(plate, GetNullableString(reader, "make"), GetNullableString(reader, "model"), GetNullableDecimal(reader, "catalog_price"), GetNullableInt32(reader, "registration_year"), GetNullableString(reader, "fuel_description"), GetNullableString(reader, "body_type"))
            : null;
        return new Sighting(
            reader.GetInt64(reader.GetOrdinal("id")),
            plate,
            reader.GetString(reader.GetOrdinal("display_plate")),
            GetNullableString(reader, "region"),
            ParseTimestamp(reader.GetString(reader.GetOrdinal("first_seen_at"))),
            ParseTimestamp(reader.GetString(reader.GetOrdinal("last_seen_at"))),
            reader.GetFloat(reader.GetOrdinal("confidence")),
            reader.GetInt32(reader.GetOrdinal("observation_count")),
            location,
            vehicle)
        {
            TripId = GetNullableInt64(reader, "trip_id"),
            SnapshotReference = GetNullableString(reader, "snapshot_reference")
        };
    }

    private static bool HasVehicle(SqliteDataReader reader) => !reader.IsDBNull(reader.GetOrdinal("make")) || !reader.IsDBNull(reader.GetOrdinal("model")) || !reader.IsDBNull(reader.GetOrdinal("catalog_price"));
    private static GeoPoint? ReadLocation(SqliteDataReader reader, string latitude, string longitude, string accuracy) => ReadLocation(reader, reader.GetOrdinal(latitude), reader.GetOrdinal(longitude), reader.GetOrdinal(accuracy));
    private static GeoPoint? ReadLocation(SqliteDataReader reader, int latitude, int longitude, int accuracy) => reader.IsDBNull(latitude) || reader.IsDBNull(longitude) ? null : new GeoPoint(reader.GetDouble(latitude), reader.GetDouble(longitude), reader.IsDBNull(accuracy) ? null : reader.GetFloat(accuracy));
    private static string? GetNullableString(SqliteDataReader reader, string name) => GetNullableString(reader, reader.GetOrdinal(name));
    private static string? GetNullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static int? GetNullableInt32(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt32(reader.GetOrdinal(name));
    private static int? GetNullableInt32(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static long? GetNullableInt64(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt64(reader.GetOrdinal(name));
    private static decimal? GetNullableDecimal(SqliteDataReader reader, string name) => GetNullableDecimal(reader, reader.GetOrdinal(name));
    private static decimal? GetNullableDecimal(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static object DbValue<T>(T? value) => value is null ? DBNull.Value : value;

    private static double DistanceMeters(GeoPoint from, GeoPoint to)
    {
        const double earthRadius = 6_371_000;
        var latitudeDelta = DegreesToRadians(to.Latitude - from.Latitude);
        var longitudeDelta = DegreesToRadians(to.Longitude - from.Longitude);
        var a = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2) + Math.Cos(DegreesToRadians(from.Latitude)) * Math.Cos(DegreesToRadians(to.Latitude)) * Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        return earthRadius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180;

    private static async Task<bool> HasColumnAsync(SqliteConnection connection, string table, string column, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS trips (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            started_at TEXT NOT NULL,
            ended_at TEXT NULL,
            distance_meters REAL NOT NULL DEFAULT 0,
            start_latitude REAL NULL,
            start_longitude REAL NULL,
            start_accuracy_meters REAL NULL,
            end_latitude REAL NULL,
            end_longitude REAL NULL,
            end_accuracy_meters REAL NULL
        );
        CREATE TABLE IF NOT EXISTS sightings (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            normalized_plate TEXT NOT NULL,
            display_plate TEXT NOT NULL,
            region TEXT NULL,
            first_seen_at TEXT NOT NULL,
            last_seen_at TEXT NOT NULL,
            confidence REAL NOT NULL,
            observation_count INTEGER NOT NULL,
            latitude REAL NULL,
            longitude REAL NULL,
            location_accuracy_meters REAL NULL,
            make TEXT NULL,
            model TEXT NULL,
            catalog_price NUMERIC NULL,
            registration_year INTEGER NULL,
            fuel_description TEXT NULL,
            body_type TEXT NULL,
            trip_id INTEGER NULL REFERENCES trips(id) ON DELETE SET NULL,
            snapshot_reference TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS trip_points (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            trip_id INTEGER NOT NULL REFERENCES trips(id) ON DELETE CASCADE,
            recorded_at TEXT NOT NULL,
            latitude REAL NOT NULL,
            longitude REAL NOT NULL,
            accuracy_meters REAL NULL
        );
        """;

    private const string Indexes = """
        CREATE INDEX IF NOT EXISTS ix_sightings_plate_last_seen ON sightings(normalized_plate, last_seen_at DESC);
        CREATE INDEX IF NOT EXISTS ix_sightings_price ON sightings(catalog_price DESC) WHERE catalog_price IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_sightings_trip_last_seen ON sightings(trip_id, last_seen_at DESC);
        CREATE INDEX IF NOT EXISTS ix_trip_points_trip_time ON trip_points(trip_id, recorded_at);
        CREATE INDEX IF NOT EXISTS ix_trips_started ON trips(started_at DESC);
        """;
}
