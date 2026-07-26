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
    }

    public async Task<Sighting> AddOrMergeAsync(
        ConfirmedPlate plate,
        GeoPoint? location,
        VehicleRecord? vehicle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plate);
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var cutoff = plate.FirstSeenAt - _mergeWindow;
        var existingId = await FindMergeCandidateAsync(
            connection,
            transaction,
            plate.Consensus.NormalizedPlate,
            cutoff,
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
            AddCommonParameters(update, plate, location, vehicle);
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
                    make, model, catalog_price, registration_year, fuel_description, body_type)
                VALUES (
                    @normalized_plate, @display_plate, @region, @first_seen_at, @last_seen_at,
                    @confidence, @observation_count, @latitude, @longitude, @accuracy,
                    @make, @model, @catalog_price, @registration_year, @fuel, @body_type);
                SELECT last_insert_rowid();
                """;
            AddCommonParameters(insert, plate, location, vehicle);
            id = (long)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("SQLite did not return a sighting identifier."));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<Sighting>> GetRecentAsync(int limit, CancellationToken cancellationToken) =>
        QueryAsync(
            "SELECT * FROM sightings ORDER BY last_seen_at DESC LIMIT @limit;",
            command => command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000)),
            cancellationToken);

    public Task<IReadOnlyList<Sighting>> FindByPlateAsync(string normalizedPlate, CancellationToken cancellationToken) =>
        QueryAsync(
            "SELECT * FROM sightings WHERE normalized_plate = @plate ORDER BY last_seen_at DESC;",
            command => command.Parameters.AddWithValue("@plate", PlateText.Normalize(normalizedPlate)),
            cancellationToken);

    public async Task<Sighting?> GetMostExpensiveAsync(CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(
            "SELECT * FROM sightings WHERE catalog_price IS NOT NULL ORDER BY catalog_price DESC, last_seen_at DESC LIMIT 1;",
            null,
            cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    private async Task<Sighting> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(
            "SELECT * FROM sightings WHERE id = @id;",
            command => command.Parameters.AddWithValue("@id", id),
            cancellationToken).ConfigureAwait(false);
        return rows.Single();
    }

    private async Task<IReadOnlyList<Sighting>> QueryAsync(
        string sql,
        Action<SqliteCommand>? configure,
        CancellationToken cancellationToken)
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

    private static async Task<long?> FindMergeCandidateAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string normalizedPlate,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            SELECT id FROM sightings
            WHERE normalized_plate = @plate AND last_seen_at >= @cutoff
            ORDER BY last_seen_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("@plate", normalizedPlate);
        command.Parameters.AddWithValue("@cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static void AddCommonParameters(
        SqliteCommand command,
        ConfirmedPlate plate,
        GeoPoint? location,
        VehicleRecord? vehicle)
    {
        command.Parameters.AddWithValue("@normalized_plate", plate.Consensus.NormalizedPlate);
        command.Parameters.AddWithValue("@display_plate", plate.Consensus.DisplayPlate);
        command.Parameters.AddWithValue("@region", DbValue(plate.Consensus.Region));
        command.Parameters.AddWithValue("@first_seen_at", plate.FirstSeenAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@last_seen_at", plate.LastSeenAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@confidence", plate.Consensus.Confidence);
        command.Parameters.AddWithValue("@observation_count", plate.Consensus.ObservationCount);
        command.Parameters.AddWithValue("@latitude", DbValue(location?.Latitude));
        command.Parameters.AddWithValue("@longitude", DbValue(location?.Longitude));
        command.Parameters.AddWithValue("@accuracy", DbValue(location?.AccuracyMeters));
        command.Parameters.AddWithValue("@make", DbValue(vehicle?.Make));
        command.Parameters.AddWithValue("@model", DbValue(vehicle?.Model));
        command.Parameters.AddWithValue("@catalog_price", DbValue(vehicle?.CatalogPrice));
        command.Parameters.AddWithValue("@registration_year", DbValue(vehicle?.RegistrationYear));
        command.Parameters.AddWithValue("@fuel", DbValue(vehicle?.FuelDescription));
        command.Parameters.AddWithValue("@body_type", DbValue(vehicle?.BodyType));
    }

    private static Sighting ReadSighting(SqliteDataReader reader)
    {
        GeoPoint? location = reader.IsDBNull(reader.GetOrdinal("latitude")) || reader.IsDBNull(reader.GetOrdinal("longitude"))
            ? null
            : new GeoPoint(
                reader.GetDouble(reader.GetOrdinal("latitude")),
                reader.GetDouble(reader.GetOrdinal("longitude")),
                GetNullableFloat(reader, "location_accuracy_meters"));
        var plate = reader.GetString(reader.GetOrdinal("normalized_plate"));
        var vehicle = HasVehicle(reader)
            ? new VehicleRecord(
                plate,
                GetNullableString(reader, "make"),
                GetNullableString(reader, "model"),
                GetNullableDecimal(reader, "catalog_price"),
                GetNullableInt32(reader, "registration_year"),
                GetNullableString(reader, "fuel_description"),
                GetNullableString(reader, "body_type"))
            : null;

        return new Sighting(
            reader.GetInt64(reader.GetOrdinal("id")),
            plate,
            reader.GetString(reader.GetOrdinal("display_plate")),
            GetNullableString(reader, "region"),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("first_seen_at")), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("last_seen_at")), CultureInfo.InvariantCulture),
            reader.GetFloat(reader.GetOrdinal("confidence")),
            reader.GetInt32(reader.GetOrdinal("observation_count")),
            location,
            vehicle);
    }

    private static bool HasVehicle(SqliteDataReader reader) =>
        !reader.IsDBNull(reader.GetOrdinal("make")) ||
        !reader.IsDBNull(reader.GetOrdinal("model")) ||
        !reader.IsDBNull(reader.GetOrdinal("catalog_price"));

    private static string? GetNullableString(SqliteDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));

    private static int? GetNullableInt32(SqliteDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt32(reader.GetOrdinal(name));

    private static float? GetNullableFloat(SqliteDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetFloat(reader.GetOrdinal(name));

    private static decimal? GetNullableDecimal(SqliteDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetDecimal(reader.GetOrdinal(name));

    private static object DbValue<T>(T? value) => value is null ? DBNull.Value : value;

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string Schema = """
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
            body_type TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_sightings_plate_last_seen
            ON sightings(normalized_plate, last_seen_at DESC);
        CREATE INDEX IF NOT EXISTS ix_sightings_price
            ON sightings(catalog_price DESC) WHERE catalog_price IS NOT NULL;
        PRAGMA user_version = 1;
        """;
}
