using DeveMobileLPR.Application;
using Windows.Devices.Enumeration;

namespace DeveMobileLPR.App.Platforms.Windows.Camera;

internal sealed class WindowsDriveSourceCatalog : IDriveSourceCatalog
{
    private static readonly IReadOnlyList<VideoResolution> Resolutions =
    [
        new(3840, 2160),
        new(1920, 1080),
        new(1280, 720)
    ];

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
                Resolutions)),
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
