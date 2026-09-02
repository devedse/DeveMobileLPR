namespace DeveMobileLPR.App.Services;

internal interface ITripCardGestureAdapter
{
    bool HandlesTap { get; }
    void Attach(Border card, Action longPressed, Action tapped);
}

internal sealed class PassiveTripCardGestureAdapter : ITripCardGestureAdapter
{
    public bool HandlesTap => false;
    public void Attach(Border card, Action longPressed, Action tapped)
    {
    }
}

internal interface ICameraCapabilitiesLauncher
{
    Task ShowAsync();
}

internal sealed class UnsupportedCameraCapabilitiesLauncher : ICameraCapabilitiesLauncher
{
    public Task ShowAsync() => Task.CompletedTask;
}
