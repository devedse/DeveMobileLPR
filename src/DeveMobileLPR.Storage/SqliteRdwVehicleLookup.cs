using DeveMobileLPR.Recognition;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DeveMobileLPR.Storage;

public sealed class SqliteRdwVehicleLookup : IVehicleLookup
{
    public const string RequiredView = "rdw_vehicles";
    private readonly string _databasePath;
    private readonly DbContextOptions<RdwDbContext> _options;

    public SqliteRdwVehicleLookup(string rdwDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rdwDatabasePath);
        _databasePath = Path.GetFullPath(rdwDatabasePath);
        _options = new DbContextOptionsBuilder<RdwDbContext>()
            .UseSqlite(ReadOnlyConnectionString(_databasePath))
            .Options;
    }

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        await using var db = new RdwDbContext(_options);
        try
        {
            await db.Vehicles.AsNoTracking()
                .Select(v => v.NormalizedPlate)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
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
        if (plate.Length == 0 || !File.Exists(_databasePath))
        {
            return null;
        }

        await using var db = new RdwDbContext(_options);
        var vehicle = await db.Vehicles.AsNoTracking()
            .Where(v => v.NormalizedPlate == plate)
            .Select(v => new VehicleRecord(
                v.NormalizedPlate,
                v.Make,
                v.Model,
                v.CatalogPrice,
                v.RegistrationYear,
                v.FuelDescription,
                v.BodyType))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return vehicle;
    }

    internal static string ReadOnlyConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
}
