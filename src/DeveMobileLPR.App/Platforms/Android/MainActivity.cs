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
    protected override void OnStop()
    {
        if (!IsChangingConfigurations && IPlatformApplication.Current?.Services.GetService<Services.DriveCoordinator>() is { Snapshot.IsDriving: true } coordinator)
        {
            _ = coordinator.StopDriveAsync();
        }
        base.OnStop();
    }
}
