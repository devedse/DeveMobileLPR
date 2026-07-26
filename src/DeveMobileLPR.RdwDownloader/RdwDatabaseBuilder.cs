using System.Globalization;
using DeveMobileLPR.Recognition;
using Microsoft.Data.Sqlite;

namespace DeveMobileLPR.RdwDownloader;

internal sealed class RdwDatabaseBuilder : IAsyncDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _connection;

    private RdwDatabaseBuilder(string databasePath, SqliteConnection connection)
    {
        DatabasePath = databasePath;
        _connection = connection;
    }

    public string DatabasePath { get; }

    public static async Task<RdwDatabaseBuilder> OpenAsync(
        string databasePath,
        DatasetSnapshot vehicleSnapshot,
        DatasetSnapshot fuelSnapshot,
        long? sampleLimit,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var existed = File.Exists(fullPath);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 60
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var builder = new RdwDatabaseBuilder(fullPath, connection);
        try
        {
            await builder.ConfigureAsync(cancellationToken).ConfigureAwait(false);
            if (existed)
            {
                await builder.ValidateResumeAsync(vehicleSnapshot, fuelSnapshot, sampleLimit, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await builder.CreateSchemaAsync(vehicleSnapshot, fuelSnapshot, sampleLimit, cancellationToken).ConfigureAwait(false);
            }

            return builder;
        }
        catch
        {
            await builder.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ImportState> GetStateAsync(string datasetId, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT dataset_id, rows_updated_at, last_plate, last_sequence,
                   imported_rows, completed, sample_limit
            FROM rdw_import_state WHERE dataset_id = @dataset_id;
            """;
        command.Parameters.AddWithValue("@dataset_id", datasetId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException($"Partial RDW database has no state for dataset '{datasetId}'. Use --restart.");
        }

        return new ImportState(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt64(5) != 0,
            reader.IsDBNull(6) ? null : reader.GetInt64(6));
    }

    public void CommitVehiclePage(IReadOnlyList<VehicleSourceRow> rows, bool completed)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            MarkCompleted(RdwDatasets.Vehicles);
            return;
        }

        using var transaction = _connection.BeginTransaction();
        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO rdw_vehicle_data (
                normalized_plate, make, model, catalog_price, registration_year,
                fuel_primary, fuel_secondary, body_type)
            VALUES (@plate, @make, @model, @price, @year, NULL, NULL, @body_type)
            ON CONFLICT(normalized_plate) DO UPDATE SET
                make = excluded.make,
                model = excluded.model,
                catalog_price = excluded.catalog_price,
                registration_year = excluded.registration_year,
                body_type = excluded.body_type;
            """;
        var plate = insert.Parameters.Add("@plate", SqliteType.Text);
        var make = insert.Parameters.Add("@make", SqliteType.Text);
        var model = insert.Parameters.Add("@model", SqliteType.Text);
        var price = insert.Parameters.Add("@price", SqliteType.Integer);
        var year = insert.Parameters.Add("@year", SqliteType.Integer);
        var bodyType = insert.Parameters.Add("@body_type", SqliteType.Text);
        insert.Prepare();

        foreach (var row in rows)
        {
            var normalized = PlateText.Normalize(row.CursorPlate);
            if (normalized.Length == 0)
            {
                throw new InvalidDataException("RDW returned an empty vehicle plate.");
            }

            plate.Value = normalized;
            make.Value = DbValue(row.Make);
            model.Value = DbValue(row.Model);
            price.Value = DbValue(row.CatalogPrice);
            year.Value = DbValue(row.RegistrationYear);
            bodyType.Value = DbValue(row.BodyType);
            insert.ExecuteNonQuery();
        }

        UpdateState(
            transaction,
            RdwDatasets.Vehicles,
            rows[^1].CursorPlate,
            null,
            rows.Count,
            completed);
        transaction.Commit();
    }

    public void CommitFuelPage(IReadOnlyList<FuelSourceRow> rows, bool completed)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            MarkCompleted(RdwDatasets.Fuels);
            return;
        }

        using var transaction = _connection.BeginTransaction();
        using var update = _connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE rdw_vehicle_data
            SET fuel_primary = CASE
                    WHEN @sequence = '1' AND @description IS NOT NULL THEN @description
                    ELSE fuel_primary
                END,
                fuel_secondary = CASE
                    WHEN @sequence = '1' OR @description IS NULL THEN fuel_secondary
                    WHEN fuel_secondary IS NULL OR fuel_secondary = '' THEN @description
                    WHEN instr(' / ' || fuel_secondary || ' / ', ' / ' || @description || ' / ') > 0 THEN fuel_secondary
                    ELSE fuel_secondary || ' / ' || @description
                END
            WHERE normalized_plate = @plate;
            """;
        var plate = update.Parameters.Add("@plate", SqliteType.Text);
        var sequence = update.Parameters.Add("@sequence", SqliteType.Text);
        var description = update.Parameters.Add("@description", SqliteType.Text);
        update.Prepare();

        foreach (var row in rows)
        {
            var normalized = PlateText.Normalize(row.CursorPlate);
            if (normalized.Length == 0 || row.CursorSequence.Length == 0)
            {
                throw new InvalidDataException("RDW returned an invalid fuel cursor.");
            }

            plate.Value = normalized;
            sequence.Value = row.CursorSequence;
            description.Value = DbValue(row.Description);
            update.ExecuteNonQuery();
        }

        UpdateState(
            transaction,
            RdwDatasets.Fuels,
            rows[^1].CursorPlate,
            rows[^1].CursorSequence,
            rows.Count,
            completed);
        transaction.Commit();
    }

    public async Task<(long VehicleRows, long FuelRows, long VehiclesWithFuel)> FinalizeAsync(
        long expectedVehicleRows,
        long expectedFuelRows,
        bool isSample,
        CancellationToken cancellationToken)
    {
        var vehicleState = await GetStateAsync(RdwDatasets.Vehicles, cancellationToken).ConfigureAwait(false);
        var fuelState = await GetStateAsync(RdwDatasets.Fuels, cancellationToken).ConfigureAwait(false);
        if (!vehicleState.Completed || !fuelState.Completed)
        {
            throw new InvalidOperationException("Cannot finalize an incomplete RDW import.");
        }

        if (!isSample &&
            (vehicleState.ImportedRows != expectedVehicleRows || fuelState.ImportedRows != expectedFuelRows))
        {
            throw new InvalidDataException(
                $"RDW source counts changed or rows were missed. Expected {expectedVehicleRows:N0}/{expectedFuelRows:N0}, " +
                $"imported {vehicleState.ImportedRows:N0}/{fuelState.ImportedRows:N0}.");
        }

        var vehicleRows = await ScalarInt64Async(
            "SELECT count(*) FROM rdw_vehicle_data;",
            cancellationToken).ConfigureAwait(false);
        if (vehicleRows != vehicleState.ImportedRows)
        {
            throw new InvalidDataException(
                $"RDW vehicle key validation failed: processed {vehicleState.ImportedRows:N0} unique source rows but stored {vehicleRows:N0} rows.");
        }

        var vehiclesWithFuel = await ScalarInt64Async(
            "SELECT count(*) FROM rdw_vehicle_data WHERE fuel_primary IS NOT NULL OR fuel_secondary IS NOT NULL;",
            cancellationToken).ConfigureAwait(false);
        var quickCheck = await ScalarStringAsync("PRAGMA quick_check;", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SQLite quick_check failed: {quickCheck}");
        }

        await using (var metadata = _connection.CreateCommand())
        {
            metadata.CommandText = """
                INSERT INTO rdw_import_metadata (
                    singleton, generated_at_utc, vehicle_dataset_id, vehicle_rows_updated_at,
                    vehicle_source_rows, fuel_dataset_id, fuel_rows_updated_at, fuel_source_rows,
                    imported_vehicle_rows, imported_fuel_rows, vehicles_with_fuel, is_sample)
                VALUES (
                    1, @generated, @vehicle_id, @vehicle_updated, @vehicle_source_rows,
                    @fuel_id, @fuel_updated, @fuel_source_rows,
                    @vehicle_rows, @fuel_rows, @vehicles_with_fuel, @is_sample)
                ON CONFLICT(singleton) DO UPDATE SET
                    generated_at_utc = excluded.generated_at_utc,
                    vehicle_dataset_id = excluded.vehicle_dataset_id,
                    vehicle_rows_updated_at = excluded.vehicle_rows_updated_at,
                    vehicle_source_rows = excluded.vehicle_source_rows,
                    fuel_dataset_id = excluded.fuel_dataset_id,
                    fuel_rows_updated_at = excluded.fuel_rows_updated_at,
                    fuel_source_rows = excluded.fuel_source_rows,
                    imported_vehicle_rows = excluded.imported_vehicle_rows,
                    imported_fuel_rows = excluded.imported_fuel_rows,
                    vehicles_with_fuel = excluded.vehicles_with_fuel,
                    is_sample = excluded.is_sample;
                """;
            metadata.Parameters.AddWithValue("@generated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            metadata.Parameters.AddWithValue("@vehicle_id", RdwDatasets.Vehicles);
            metadata.Parameters.AddWithValue("@vehicle_updated", vehicleState.RowsUpdatedAt);
            metadata.Parameters.AddWithValue("@vehicle_source_rows", expectedVehicleRows);
            metadata.Parameters.AddWithValue("@fuel_id", RdwDatasets.Fuels);
            metadata.Parameters.AddWithValue("@fuel_updated", fuelState.RowsUpdatedAt);
            metadata.Parameters.AddWithValue("@fuel_source_rows", expectedFuelRows);
            metadata.Parameters.AddWithValue("@vehicle_rows", vehicleRows);
            metadata.Parameters.AddWithValue("@fuel_rows", fuelState.ImportedRows);
            metadata.Parameters.AddWithValue("@vehicles_with_fuel", vehiclesWithFuel);
            metadata.Parameters.AddWithValue("@is_sample", isSample ? 1 : 0);
            await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync("ANALYZE rdw_vehicle_data; PRAGMA optimize;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync("PRAGMA journal_mode=DELETE;", cancellationToken).ConfigureAwait(false);
        return (vehicleRows, fuelState.ImportedRows, vehiclesWithFuel);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private async Task ConfigureAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            """
            PRAGMA busy_timeout=60000;
            PRAGMA foreign_keys=ON;
            PRAGMA temp_store=MEMORY;
            PRAGMA cache_size=-262144;
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA wal_autocheckpoint=10000;
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateSchemaAsync(
        DatasetSnapshot vehicleSnapshot,
        DatasetSnapshot fuelSnapshot,
        long? sampleLimit,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            $"""
            PRAGMA application_id=1145917522;
            PRAGMA user_version={SchemaVersion};

            CREATE TABLE rdw_vehicle_data (
                normalized_plate TEXT NOT NULL PRIMARY KEY,
                make TEXT,
                model TEXT,
                catalog_price INTEGER,
                registration_year INTEGER,
                fuel_primary TEXT,
                fuel_secondary TEXT,
                body_type TEXT
            ) WITHOUT ROWID;

            CREATE VIEW rdw_vehicles AS
            SELECT normalized_plate,
                   make,
                   model,
                   catalog_price,
                   registration_year,
                   CASE
                       WHEN fuel_primary IS NOT NULL AND fuel_secondary IS NOT NULL
                           THEN fuel_primary || ' / ' || fuel_secondary
                       ELSE COALESCE(fuel_primary, fuel_secondary)
                   END AS fuel_description,
                   body_type
            FROM rdw_vehicle_data;

            CREATE TABLE rdw_import_state (
                dataset_id TEXT NOT NULL PRIMARY KEY,
                rows_updated_at INTEGER NOT NULL,
                last_plate TEXT,
                last_sequence TEXT,
                imported_rows INTEGER NOT NULL DEFAULT 0,
                completed INTEGER NOT NULL DEFAULT 0,
                sample_limit INTEGER
            ) WITHOUT ROWID;

            CREATE TABLE rdw_import_metadata (
                singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
                generated_at_utc TEXT NOT NULL,
                vehicle_dataset_id TEXT NOT NULL,
                vehicle_rows_updated_at INTEGER NOT NULL,
                vehicle_source_rows INTEGER NOT NULL,
                fuel_dataset_id TEXT NOT NULL,
                fuel_rows_updated_at INTEGER NOT NULL,
                fuel_source_rows INTEGER NOT NULL,
                imported_vehicle_rows INTEGER NOT NULL,
                imported_fuel_rows INTEGER NOT NULL,
                vehicles_with_fuel INTEGER NOT NULL,
                is_sample INTEGER NOT NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);

        await InsertInitialStateAsync(vehicleSnapshot, sampleLimit, cancellationToken).ConfigureAwait(false);
        await InsertInitialStateAsync(fuelSnapshot, sampleLimit, cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertInitialStateAsync(
        DatasetSnapshot snapshot,
        long? sampleLimit,
        CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO rdw_import_state (
                dataset_id, rows_updated_at, last_plate, last_sequence,
                imported_rows, completed, sample_limit)
            VALUES (@id, @updated, NULL, NULL, 0, 0, @sample_limit);
            """;
        command.Parameters.AddWithValue("@id", snapshot.Id);
        command.Parameters.AddWithValue("@updated", snapshot.RowsUpdatedAt);
        command.Parameters.AddWithValue("@sample_limit", DbValue(sampleLimit));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateResumeAsync(
        DatasetSnapshot vehicleSnapshot,
        DatasetSnapshot fuelSnapshot,
        long? sampleLimit,
        CancellationToken cancellationToken)
    {
        var version = await ScalarInt64Async("PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
        if (version != SchemaVersion)
        {
            throw new InvalidDataException($"Partial RDW database schema is version {version}, expected {SchemaVersion}. Use --restart.");
        }

        foreach (var snapshot in new[] { vehicleSnapshot, fuelSnapshot })
        {
            var state = await GetStateAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
            if (state.RowsUpdatedAt != snapshot.RowsUpdatedAt)
            {
                throw new InvalidDataException(
                    $"RDW dataset {snapshot.Id} changed since this partial import started. Use --restart to build a consistent snapshot.");
            }

            if (state.SampleLimit != sampleLimit)
            {
                throw new InvalidDataException("The partial import used a different --sample-rows value. Use --restart.");
            }
        }
    }

    private void MarkCompleted(string datasetId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE rdw_import_state SET completed = 1 WHERE dataset_id = @dataset_id;";
        command.Parameters.AddWithValue("@dataset_id", datasetId);
        command.ExecuteNonQuery();
    }

    private void UpdateState(
        SqliteTransaction transaction,
        string datasetId,
        string lastPlate,
        string? lastSequence,
        int importedRows,
        bool completed)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE rdw_import_state
            SET last_plate = @last_plate,
                last_sequence = @last_sequence,
                imported_rows = imported_rows + @imported_rows,
                completed = @completed
            WHERE dataset_id = @dataset_id;
            """;
        command.Parameters.AddWithValue("@last_plate", lastPlate);
        command.Parameters.AddWithValue("@last_sequence", DbValue(lastSequence));
        command.Parameters.AddWithValue("@imported_rows", importedRows);
        command.Parameters.AddWithValue("@completed", completed ? 1 : 0);
        command.Parameters.AddWithValue("@dataset_id", datasetId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException($"Partial RDW database has no state for dataset '{datasetId}'.");
        }
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> ScalarInt64Async(string sql, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private async Task<string> ScalarStringAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;
}
