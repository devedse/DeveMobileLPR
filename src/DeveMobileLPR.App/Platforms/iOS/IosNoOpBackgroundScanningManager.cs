using DeveMobileLPR.App.Services;

namespace DeveMobileLPR.App;

/// <summary>
/// iOS does not permit continuous camera capture after the app enters the
/// background, so the shared setting is exposed as unsupported.
/// </summary>
internal sealed class IosNoOpBackgroundScanningManager : IBackgroundScanningManager
{
    public bool IsSupported => false;
    public bool HasRequiredPermissions => true;
    public Task<bool> RequestPermissionsAsync() => Task.FromResult(true);
    public void Start() { }
    public void Stop() { }
}
