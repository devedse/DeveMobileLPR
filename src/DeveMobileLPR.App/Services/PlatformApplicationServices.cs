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
        Stream? unopenedStream = null;
        try
        {
            var fileName = FileName(sound);
            unopenedStream = await FileSystem.OpenAppPackageFileAsync(fileName).ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                StopKnownVehiclePlayer();
                var stream = unopenedStream
                    ?? throw new InvalidOperationException("The packaged sound stream is unavailable.");
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
                unopenedStream = null;
                player.Play();
                applicationLog.Write("Audio", $"Known-vehicle sound started: {fileName}.");
            });
        }
        catch (Exception exception)
        {
            applicationLog.Write("Audio", $"Known-vehicle sound failed: {exception}", true);
        }
        finally
        {
            unopenedStream?.Dispose();
        }
    }

    private void StopKnownVehiclePlayer()
    {
        // Clear the active references before Stop(). Some native players raise PlaybackEnded
        // synchronously from Stop; the handler must then see that this player is no longer active
        // instead of recursively entering this method until the process crashes.
        var player = _knownVehiclePlayer;
        var stream = _knownVehicleStream;
        _knownVehiclePlayer = null;
        _knownVehicleStream = null;
        player?.Stop();
        player?.Dispose();
        stream?.Dispose();
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
        KnownVehicleSound.CarHorn => "known_vehicle_car_horn.wav",
        KnownVehicleSound.CarSignal => "known_vehicle_car_signal.wav",
        KnownVehicleSound.EngineStart => "known_vehicle_engine_start.wav",
        KnownVehicleSound.DoorClose => "known_vehicle_door_close.wav",
        KnownVehicleSound.Kalimba => "known_vehicle_kalimba.wav",
        KnownVehicleSound.SteamWhistle => "known_vehicle_steam_whistle.wav",
        KnownVehicleSound.Applause => "known_vehicle_applause.wav",
        KnownVehicleSound.OrchestralChimes => "known_vehicle_orchestral_chimes.wav",
        KnownVehicleSound.BellDing => "known_vehicle_bell_ding.wav",
        _ => throw new ArgumentOutOfRangeException(nameof(sound), sound, null)
    };
}
