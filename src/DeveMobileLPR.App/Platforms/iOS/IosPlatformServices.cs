using AVFoundation;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;

namespace DeveMobileLPR.App;

internal sealed class IosDriveSourceCatalog : IDriveSourceCatalog
{
    public bool SupportsMultipleSources => false;
    public int MaximumSimultaneousIntegratedSources => 1;

    public Task<IReadOnlyList<DriveSourceCapability>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = new List<DriveSourceCapability>();
        AddCamera(sources, "rear", "Rear camera", AVCaptureDevicePosition.Back, InferredLensRole.Main);
        AddCamera(sources, "front", "Front camera", AVCaptureDevicePosition.Front, InferredLensRole.Front);
        sources.Add(new(
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
            []));
        return Task.FromResult<IReadOnlyList<DriveSourceCapability>>(sources);
    }

    private static void AddCamera(
        ICollection<DriveSourceCapability> sources,
        string id,
        string name,
        AVCaptureDevicePosition position,
        InferredLensRole role)
    {
        using var device = AVCaptureDevice.GetDefaultDevice(
            AVCaptureDeviceType.BuiltInWideAngleCamera,
            AVMediaTypes.Video,
            position);
        if (device is null) return;
        sources.Add(new(
            id,
            name,
            DriveSourceKind.LogicalCamera,
            true,
            id,
            null,
            null,
            null,
            null,
            1f,
            5f,
            [],
            role));
    }
}

internal sealed class IosPlatformSettingsInfo : IPlatformSettingsInfo
{
    public string BackgroundScanningDescription =>
        "Background camera recognition is not supported by the iPhone build.";

    public string OpenSettingsLabel => "Open iPhone app settings";

    public string PlatformDescription => "iPhone · iOS 16+ · arm64";

    public string RecognitionEngineDescription =>
        "YOLOv9 plate detector · CCT-S V2 OCR · AVFoundation NV12 capture · LiteRT / Metal · temporal consensus";

    public Task<string> GetPermissionsDetailAsync() => Task.FromResult(
        "Camera and location access are managed in iPhone Settings");

    public void OpenAppSettings() => AppInfo.Current.ShowSettingsUI();
}
