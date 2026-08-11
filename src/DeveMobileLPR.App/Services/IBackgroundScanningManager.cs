namespace DeveMobileLPR.App.Services;

internal interface IBackgroundScanningManager
{
    bool IsSupported { get; }
    bool HasRequiredPermissions { get; }
    Task<bool> RequestPermissionsAsync();
    void Start();
    void Stop();
}
