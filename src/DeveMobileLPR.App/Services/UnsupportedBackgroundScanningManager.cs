namespace DeveMobileLPR.App.Services;

/// <summary>
/// Shared fallback for targets that cannot continue scanning while the app is backgrounded.
/// </summary>
internal sealed class UnsupportedBackgroundScanningManager : IBackgroundScanningManager
{
    public bool IsSupported => false;
    public bool HasRequiredPermissions => true;
    public Task<bool> RequestPermissionsAsync() => Task.FromResult(true);
    public void Start() { }
    public void Stop() { }
}
