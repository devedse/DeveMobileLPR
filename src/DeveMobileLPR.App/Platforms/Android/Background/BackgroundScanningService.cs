using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using DeveMobileLPR.App.Platforms.Android.Camera;
using DeveMobileLPR.Application;
using System.Runtime.Versioning;

namespace DeveMobileLPR.App.Platforms.Android.Background;

[Service(
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeCamera | ForegroundService.TypeLocation)]
internal sealed class BackgroundScanningService : Service
{
    private const int NotificationId = 1907;
    private const string NotificationChannelId = "active-drive";
    private const string StopAction = "nl.deve.mobilelpr.action.STOP_BACKGROUND_DRIVE";

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
        StartInForeground();
        Services.GetRequiredService<AndroidCameraLifecycleOwner>().SetServiceActive(true);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == StopAction)
        {
            _ = StopDriveAsync();
        }
        else
        {
            // Refresh the active types after drive startup has had a chance to request
            // the optional location permission.
            StartInForeground();
        }
        return StartCommandResult.NotSticky;
    }

    public override global::Android.OS.IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        Services.GetRequiredService<AndroidCameraLifecycleOwner>().SetServiceActive(false);
        base.OnDestroy();
    }

    private IServiceProvider Services => IPlatformApplication.Current?.Services
        ?? throw new InvalidOperationException("Application services are unavailable.");

    private async Task StopDriveAsync()
    {
        try
        {
            await Services.GetRequiredService<DriveCoordinator>().StopDriveAsync();
        }
        finally
        {
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
    }

    private void StartInForeground()
    {
        var launchIntent = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        var launchPendingIntent = PendingIntent.GetActivity(
            this,
            0,
            launchIntent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var stopIntent = new Intent(this, typeof(BackgroundScanningService)).SetAction(StopAction);
        var stopPendingIntent = PendingIntent.GetService(
            this,
            1,
            stopIntent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(this, NotificationChannelId);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle("Drive scanning is active");
        builder.SetContentText("Cars keep being recognized while DeveMobileLPR is in the background.");
        builder.SetContentIntent(launchPendingIntent);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetCategory(NotificationCompat.CategoryService);
        builder.AddAction(0, "Stop drive", stopPendingIntent);
        var notification = builder.Build()
            ?? throw new InvalidOperationException("Could not create the active-drive notification.");

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            StartTypedForeground(notification);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
    }

    [SupportedOSPlatform("android30.0")]
    private void StartTypedForeground(Notification notification)
    {
        var serviceTypes = ForegroundService.TypeCamera;
        if (Services.GetRequiredService<DeveMobileLPR.App.Services.AppSettings>().TrackLocation
            && HasLocationPermission())
        {
            serviceTypes |= ForegroundService.TypeLocation;
        }
        StartForeground(NotificationId, notification, serviceTypes);
    }

    private bool HasLocationPermission() =>
        ContextCompat.CheckSelfPermission(this, global::Android.Manifest.Permission.AccessFineLocation) == Permission.Granted
        || ContextCompat.CheckSelfPermission(this, global::Android.Manifest.Permission.AccessCoarseLocation) == Permission.Granted;

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            return;
        }

        var channel = new NotificationChannel(
            NotificationChannelId,
            "Active drive",
            NotificationImportance.Low)
        {
            Description = "Shown while car recognition continues in the background."
        };
        (GetSystemService(NotificationService) as NotificationManager)?.CreateNotificationChannel(channel);
    }
}
