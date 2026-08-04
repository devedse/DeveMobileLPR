using DeveMobileLPR.Application;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.Services;

internal sealed class MauiApplicationDispatcher : IApplicationDispatcher
{
    public void Dispatch(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        MainThread.BeginInvokeOnMainThread(action);
    }
}

internal sealed class MauiDeviceExperience : IDeviceExperience
{
    public void SetKeepScreenOn(bool enabled) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                DeviceDisplay.Current.KeepScreenOn = enabled;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not change keep-screen-on state: {exception}");
            }
        });

    public void NotifyPlateConfirmed() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                HapticFeedback.Default.Perform(HapticFeedbackType.Click);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not perform confirmation haptic: {exception}");
            }
        });
}

internal sealed class NoOpDriveLocationTrackerFactory : IDriveLocationTrackerFactory
{
    public IDriveLocationTracker Create() => new NoOpDriveLocationTracker();
}

internal sealed class NoOpDriveLocationTracker : IDriveLocationTracker
{
    public LocationFix? Latest => null;
    public Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public void Stop() { }
    public void Dispose() { }
}
