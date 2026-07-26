using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.AndroidApp.Infrastructure;

internal sealed class AppVehicleLookup(string databasePath) : IVehicleLookup
{
    public ValueTask<VehicleRecord?> FindAsync(string normalizedPlate, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return ValueTask.FromResult<VehicleRecord?>(null);
        }

        return new SqliteRdwVehicleLookup(databasePath).FindAsync(normalizedPlate, cancellationToken);
    }
}
