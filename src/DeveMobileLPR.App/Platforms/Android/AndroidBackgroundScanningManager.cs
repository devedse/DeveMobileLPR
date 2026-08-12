using Android.Content;
using AndroidX.Core.Content;
using DeveMobileLPR.App.Services;
using Android.Content.PM;

namespace DeveMobileLPR.App;

internal sealed class AndroidBackgroundScanningManager : IBackgroundScanningManager
{
    private readonly Context _context = global::Android.App.Application.Context;

    public bool IsSupported => true;
    public bool HasRequiredPermissions =>
        ContextCompat.CheckSelfPermission(_context, global::Android.Manifest.Permission.Camera) == Permission.Granted;

    public async Task<bool> RequestPermissionsAsync()
    {
        var camera = await Permissions.RequestAsync<Permissions.Camera>();
        await Permissions.RequestAsync<Permissions.PostNotifications>();
        return camera == PermissionStatus.Granted;
    }

    public void Start()
    {
        var intent = new Intent(_context, typeof(BackgroundScanningService));
        ContextCompat.StartForegroundService(_context, intent);
    }

    public void Stop()
    {
        _context.StopService(new Intent(_context, typeof(BackgroundScanningService)));
    }
}
