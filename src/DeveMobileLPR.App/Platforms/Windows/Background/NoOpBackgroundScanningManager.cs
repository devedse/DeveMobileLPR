using DeveMobileLPR.App.Services;

namespace DeveMobileLPR.App.Platforms.Windows.Background;

internal sealed class NoOpBackgroundScanningManager : IBackgroundScanningManager
{
    public bool IsSupported => false;
    public bool HasRequiredPermissions => true;
    public Task<bool> RequestPermissionsAsync() => Task.FromResult(true);
    public void Start() { }
    public void Stop() { }
}
