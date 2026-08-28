using DeveMobileLPR.Application;
using Plugin.Maui.Audio;

namespace DeveMobileLPR.App.Services;

internal sealed class MauiApplicationDispatcher : IApplicationDispatcher
{
    public void Dispatch(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        MainThread.BeginInvokeOnMainThread(action);
    }
}

internal sealed class MauiDeviceExperience(IAudioManager audioManager) : IDeviceExperience
{
    private readonly SemaphoreSlim _audioGate = new(1, 1);

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

    public void NotifyKnownVehicle(KnownVehicleSound sound)
    {
        if (sound == KnownVehicleSound.None)
        {
            return;
        }

        _ = PlayKnownVehicleAsync(sound);
    }

    private async Task PlayKnownVehicleAsync(KnownVehicleSound sound)
    {
        await _audioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync(FileName(sound));
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                using var player = audioManager.CreateAsyncPlayer(stream);
                player.Volume = 1.0;
                await player.PlayAsync(CancellationToken.None);
            });
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not play the known-vehicle sound: {exception}");
        }
        finally
        {
            _audioGate.Release();
        }
    }

    private static string FileName(KnownVehicleSound sound) => sound switch
    {
        KnownVehicleSound.Chime => "known_vehicle_chime.wav",
        KnownVehicleSound.Radar => "known_vehicle_radar.wav",
        KnownVehicleSound.Sparkle => "known_vehicle_sparkle.wav",
        _ => throw new ArgumentOutOfRangeException(nameof(sound), sound, null)
    };
}
