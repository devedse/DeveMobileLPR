using System.Data.Common;
using DeveMobileLPR.Recognition;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DeveMobileLPR.Storage;

/// <summary>Looks plates up in the downloadable RDW database through EF Core.</summary>
public sealed class RdwVehicleLookup : IVehicleLookup
{
    public const string RequiredView = "rdw_vehicles";

    private readonly string _databasePath;
    private readonly DbContextOptions<RdwDbContext> _options;

    public RdwVehicleLookup(string rdwDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rdwDatabasePath);
        _databasePath = Path.GetFullPath(rdwDatabasePath);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true
        }.ToString();
        _options = new DbContextOptionsBuilder<RdwDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        await using var context = new RdwDbContext(_options);
        try
        {
            await context.Vehicles.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            throw new InvalidDataException(
                $"The RDW database must expose a view named '{RequiredView}' with normalized_plate, make, model, "
                    + "catalog_price, registration_year, fuel_description and body_type columns.",
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

        await using var context = new RdwDbContext(_options);
        var row = await context.Vehicles
            .FirstOrDefaultAsync(vehicle => vehicle.NormalizedPlate == plate, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? null
            : new VehicleRecord(
                row.NormalizedPlate,
                row.Make,
                row.Model,
                row.CatalogPrice,
                row.RegistrationYear,
                row.FuelDescription,
                row.BodyType);
    }
}
