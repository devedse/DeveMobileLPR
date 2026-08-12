using DeveMobileLPR.App.Services;

namespace DeveMobileLPR.App.Platforms.Windows.Settings;

internal sealed class WindowsPlatformSettingsInfo : IPlatformSettingsInfo
{
    public string BackgroundScanningDescription =>
        "Background camera recognition is not supported by the Windows build.";

    public string OpenSettingsLabel => "Open Windows app settings";

    public string PlatformDescription => "Windows 10 version 2004+ · win-x64";

    public string RecognitionEngineDescription =>
        "YOLOv9 plate detector · CCT-S V2 OCR · MediaCapture high-resolution analysis · ONNX Runtime / DirectML · temporal consensus";

    public Task<string> GetPermissionsDetailAsync() => Task.FromResult(
        "Camera access is managed by Windows privacy settings · Location tracking is not supported in this build");

    public void OpenAppSettings() => AppInfo.Current.ShowSettingsUI();
}
