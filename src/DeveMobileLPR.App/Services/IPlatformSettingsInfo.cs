namespace DeveMobileLPR.App.Services;

/// <summary>
/// Supplies the settings page with platform-specific capability text and permission state without
/// leaking Android or Windows APIs into the shared view model and XAML.
/// </summary>
internal interface IPlatformSettingsInfo
{
    string BackgroundScanningDescription { get; }
    string OpenSettingsLabel { get; }
    string PlatformDescription { get; }
    string RecognitionEngineDescription { get; }

    Task<string> GetPermissionsDetailAsync();
    void OpenAppSettings();
}
