using DeveMobileLPR.App.Platforms.Android.Camera;
using DeveMobileLPR.App.Services;

namespace DeveMobileLPR.App.Platforms.Android.Settings;

internal sealed class AndroidCameraCapabilitiesLauncher : ICameraCapabilitiesLauncher
{
    public Task ShowAsync() => Shell.Current.Navigation.PushModalAsync(
        new NavigationPage(new CameraCapabilitiesPage()));
}
