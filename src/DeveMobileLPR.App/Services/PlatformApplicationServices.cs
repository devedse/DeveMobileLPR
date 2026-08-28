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

internal sealed class MauiDeviceExperience(
    IAudioManager audioManager,
    IApplicationLog applicationLog) : IDeviceExperience
{
    private IAudioPlayer? _knownVehiclePlayer;
    private Stream? _knownVehicleStream;

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

        applicationLog.Write("Audio", $"Known-vehicle sound requested: {sound}.");
        _ = PlayKnownVehicleAsync(sound);
    }

    private async Task PlayKnownVehicleAsync(KnownVehicleSound sound)
    {
        try
        {
            var fileName = FileName(sound);
            var stream = await FileSystem.OpenAppPackageFileAsync(fileName).ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                StopKnownVehiclePlayer();
                var player = audioManager.CreatePlayer(stream);
                player.Volume = 1.0;
                player.PlaybackEnded += (_, _) =>
                {
                    if (ReferenceEquals(_knownVehiclePlayer, player))
                    {
                        applicationLog.Write("Audio", $"Known-vehicle sound completed: {sound}.");
                        StopKnownVehiclePlayer();
                    }
                };
                _knownVehicleStream = stream;
                _knownVehiclePlayer = player;
                player.Play();
                applicationLog.Write("Audio", $"Known-vehicle sound started: {fileName}.");
            });
        }
        catch (Exception exception)
        {
            applicationLog.Write("Audio", $"Known-vehicle sound failed: {exception}", true);
        }
    }

    private void StopKnownVehiclePlayer()
    {
        _knownVehiclePlayer?.Stop();
        _knownVehiclePlayer?.Dispose();
        _knownVehiclePlayer = null;
        _knownVehicleStream?.Dispose();
        _knownVehicleStream = null;
    }

    private static string FileName(KnownVehicleSound sound) => sound switch
    {
        KnownVehicleSound.Chime => "known_vehicle_chime.wav",
        KnownVehicleSound.Radar => "known_vehicle_radar.wav",
        KnownVehicleSound.Sparkle => "known_vehicle_sparkle.wav",
        KnownVehicleSound.Bell => "known_vehicle_bell.wav",
        KnownVehicleSound.Confirm => "known_vehicle_confirm.wav",
        KnownVehicleSound.Glass => "known_vehicle_glass.wav",
        KnownVehicleSound.Pulse => "known_vehicle_pulse.wav",
        KnownVehicleSound.Scanner => "known_vehicle_scanner.wav",
        _ => throw new ArgumentOutOfRangeException(nameof(sound), sound, null)
    };
}
