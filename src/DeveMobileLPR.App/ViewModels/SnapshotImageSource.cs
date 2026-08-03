using DeveMobileLPR.Application;

namespace DeveMobileLPR.App.ViewModels;

internal static class SnapshotImageSource
{
    public static ImageSource? Create(IVehicleImageStore vehicleImageStore, string? reference)
    {
        var path = vehicleImageStore.ResolvePath(reference);
        return path is null
            ? null
            : ImageSource.FromStream(() => File.OpenRead(path));
    }
}