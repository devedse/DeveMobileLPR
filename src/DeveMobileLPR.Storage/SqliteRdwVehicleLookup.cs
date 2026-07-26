using DeveMobileLPR.Recognition;
using Microsoft.Data.Sqlite;

namespace DeveMobileLPR.Storage;

public sealed class SqliteRdwVehicleLookup : IVehicleLookup
{
    public const string RequiredView = "rdw_vehicles";
    private readonly SqliteConnectionFactory _connections;

    public SqliteRdwVehicleLookup(string rdwDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rdwDatabasePath);
        _connections = new SqliteConnectionFactory(rdwDatabasePath);
    }

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create(readOnly: true);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT normalized_plate, make, model, catalog_price, registration_year, fuel_description, body_type FROM rdw_vehicles LIMIT 0;";
        try
        {
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException(
                "The RDW database must expose a view named 'rdw_vehicles' with normalized_plate, make, model, catalog_price, registration_year, fuel_description and body_type columns.",
                exception);
        }
    }

    public async ValueTask<VehicleRecord?> FindAsync(string normalizedPlate, CancellationToken cancellationToken)
    {
        var plate = PlateText.Normalize(normalizedPlate);
        if (plate.Length == 0 || !File.Exists(_connections.DatabasePath))
        {
            return null;
        }

        await using var connection = _connections.Create(readOnly: true);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT normalized_plate, make, model, catalog_price, registration_year, fuel_description, body_type
            FROM rdw_vehicles WHERE normalized_plate = @plate LIMIT 1;
            """;
        command.Parameters.AddWithValue("@plate", plate);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new VehicleRecord(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDecimal(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }
}
