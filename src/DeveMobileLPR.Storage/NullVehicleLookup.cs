using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Storage;

public sealed class NullVehicleLookup : IVehicleLookup
{
    public ValueTask<VehicleRecord?> FindAsync(string normalizedPlate, CancellationToken cancellationToken) =>
        ValueTask.FromResult<VehicleRecord?>(null);
}
