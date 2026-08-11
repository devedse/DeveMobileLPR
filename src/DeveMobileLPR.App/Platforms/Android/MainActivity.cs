using Android.App;
using Android.Content.PM;

namespace DeveMobileLPR.App;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
    protected override void OnStart()
    {
        base.OnStart();
        IPlatformApplication.Current?.Services.GetService<AndroidCameraLifecycleOwner>()?.SetActivityActive(true);
    }

    protected override void OnStop()
    {
        var services = IPlatformApplication.Current?.Services;
        if (!IsChangingConfigurations && services?.GetService<DeveMobileLPR.Application.DriveCoordinator>() is { Snapshot.IsDriving: true } coordinator)
        {
            var settings = services.GetRequiredService<Services.AppSettings>();
            if (settings.ContinueScanningInBackground)
            {
                services.GetRequiredService<Services.IBackgroundScanningManager>().Start();
            }
            else
            {
                _ = coordinator.StopDriveAsync();
            }
        }
        services?.GetService<AndroidCameraLifecycleOwner>()?.SetActivityActive(false);
        base.OnStop();
    }
}
