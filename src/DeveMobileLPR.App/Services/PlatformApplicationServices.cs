using DeveMobileLPR.Application;

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
