using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.App.Infrastructure;

internal sealed class AppVehicleLookup : IVehicleLookup
{
    private readonly string _databasePath;
    private readonly RdwVehicleLookup _lookup;

    public AppVehicleLookup(string databasePath)
    {
        _databasePath = databasePath;
        // The lookup only records the path and builds its EF Core options once, so it keeps working
        // after an import replaces the file underneath it.
        _lookup = new RdwVehicleLookup(databasePath);
    }

    public ValueTask<VehicleRecord?> FindAsync(string normalizedPlate, CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            return ValueTask.FromResult<VehicleRecord?>(null);
        }

        return _lookup.FindAsync(normalizedPlate, cancellationToken);
    }
}
