using DeveMobileLPR.App.Services;

namespace DeveMobileLPR.App.Platforms.Android.Settings;

internal sealed class AndroidPlatformSettingsInfo : IPlatformSettingsInfo
{
    public string BackgroundScanningDescription =>
        "Keeps the active drive and camera recognition running behind other apps. Android shows a persistent notification until the drive stops.";

    public string OpenSettingsLabel => "Open Android app settings";

    public string PlatformDescription => "Package nl.deve.mobilelpr · Android 8.0+";

    public string RecognitionEngineDescription =>
        "YOLOv9 plate detector · CCT-S V2 OCR · CameraX high-resolution analysis · LiteRT · temporal consensus";

    public async Task<string> GetPermissionsDetailAsync()
    {
        var camera = await Permissions.CheckStatusAsync<Permissions.Camera>();
        var location = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        return $"Camera: {PermissionName(camera)} · Location: {PermissionName(location)}";
    }

    public void OpenAppSettings() => AppInfo.Current.ShowSettingsUI();

    private static string PermissionName(PermissionStatus status) => status switch
    {
        PermissionStatus.Granted => "allowed",
        PermissionStatus.Denied => "not allowed",
        _ => "not requested"
    };
}
