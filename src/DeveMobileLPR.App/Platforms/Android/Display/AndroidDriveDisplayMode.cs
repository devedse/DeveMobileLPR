using Android.Content.PM;
using Android.Views;
using DeveMobileLPR.App.Services;

namespace DeveMobileLPR.App.Platforms.Android.Display;

internal sealed class AndroidDriveDisplayMode : IDriveDisplayMode
{
    public void Apply(bool isDriving)
    {
        if (Platform.CurrentActivity is not { } activity || activity.Window?.DecorView is not { } decor)
        {
            return;
        }

#pragma warning disable CS0618
        activity.RequestedOrientation = isDriving
            ? ScreenOrientation.SensorLandscape
            : ScreenOrientation.Unspecified;
        var flags = isDriving
            ? SystemUiFlags.ImmersiveSticky
                | SystemUiFlags.Fullscreen
                | SystemUiFlags.HideNavigation
                | SystemUiFlags.LayoutFullscreen
                | SystemUiFlags.LayoutHideNavigation
                | SystemUiFlags.LayoutStable
            : SystemUiFlags.Visible;
        decor.SystemUiVisibility = (StatusBarVisibility)flags;
#pragma warning restore CS0618
    }
}
