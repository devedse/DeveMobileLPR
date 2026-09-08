namespace DeveMobileLPR.App.Services;

internal interface ICameraCapabilitiesLauncher
{
    Task ShowAsync();
}

internal sealed class UnsupportedCameraCapabilitiesLauncher : ICameraCapabilitiesLauncher
{
    public Task ShowAsync() => Task.CompletedTask;
}
