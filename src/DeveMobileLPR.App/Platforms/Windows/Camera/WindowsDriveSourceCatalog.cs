using DeveMobileLPR.Application;
using Windows.Devices.Enumeration;

namespace DeveMobileLPR.App.Platforms.Windows.Camera;

internal sealed class WindowsDriveSourceCatalog : IDriveSourceCatalog
{
    public bool SupportsMultipleSources => false;
    public int MaximumSimultaneousIntegratedSources => 1;

    public async Task<IReadOnlyList<DriveSourceCapability>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        cancellationToken.ThrowIfCancellationRequested();
        return
        [
            .. cameras.Select(camera => new DriveSourceCapability(
                camera.Id,
                camera.Name,
                DriveSourceKind.LogicalCamera,
                true,
                camera.Id,
                null,
                null,
                null,
                null,
                1f,
                4f,
                [])),
            new(
                DriveInputIds.NetworkLlHls,
                "OME LL-HLS stream",
                DriveSourceKind.NetworkLlHls,
                false,
                null,
                null,
                null,
                null,
                null,
                1f,
                1f,
                [])
        ];
    }
}
