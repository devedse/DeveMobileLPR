using DeveMobileLPR.Application;
using System.Text.Json;

namespace DeveMobileLPR.App.Services;

internal sealed class AppSettings : IDriveSettings
{
    private const string TrackLocationKey = "track_location";
    private const string SaveVehicleImagesKey = "save_vehicle_images";
    private const string ShowGuideKey = "show_road_guide";
    private const string KnownVehicleSoundKey = "known_vehicle_sound";
    private const string ZoomKey = "camera_zoom";
    private const string CameraKey = "camera_id";
    private const string RecognitionFramesPerSecondKey = "recognition_frames_per_second";
    private const string RecognitionDebugKey = "recognition_debug";
    private const string RecognitionStatisticsKey = "recognition_statistics";
    private const string ShowDriveEventLogKey = "show_drive_event_log";
    private const string ContinueScanningInBackgroundKey = "continue_scanning_in_background";
    private const string InputConfigurationKey = "drive_input_configuration_v1";
    private const int DefaultRecognitionFramesPerSecond = 4;
    private readonly object _inputConfigurationGate = new();
    private string _networkStreamUrl = string.Empty;
    private DriveInputConfiguration? _inputConfiguration;

    public bool TrackLocation
    {
        get => Preferences.Default.Get(TrackLocationKey, true);
        set => Preferences.Default.Set(TrackLocationKey, value);
    }

    public bool SaveVehicleImages
    {
        get => Preferences.Default.Get(SaveVehicleImagesKey, false);
        set => Preferences.Default.Set(SaveVehicleImagesKey, value);
    }

    public bool ShowRoadGuide
    {
        get => Preferences.Default.Get(ShowGuideKey, true);
        set => Preferences.Default.Set(ShowGuideKey, value);
    }

    public KnownVehicleSound KnownVehicleSound
    {
        get
        {
            var value = Preferences.Default.Get(KnownVehicleSoundKey, nameof(KnownVehicleSound.None));
            return Enum.TryParse<KnownVehicleSound>(value, out var sound)
                && Enum.IsDefined(sound)
                    ? sound
                    : KnownVehicleSound.None;
        }
        set => Preferences.Default.Set(
            KnownVehicleSoundKey,
            Enum.IsDefined(value) ? value.ToString() : nameof(KnownVehicleSound.None));
    }

    public float Zoom
    {
        get => Math.Clamp(Preferences.Default.Get(ZoomKey, 1f), 1f, 5f);
        set
        {
            var normalized = Math.Clamp(value, 1f, 5f);
#if ANDROID
            // The setup slider can be followed immediately by Android terminating the process.
            // Commit this tiny, user-facing value synchronously so it cannot remain in the
            // SharedPreferences apply queue and be lost during that shutdown.
            var context = global::Android.App.Application.Context;
            var preferences = context.GetSharedPreferences(
                $"{context.PackageName}_preferences",
                global::Android.Content.FileCreationMode.Private);
            preferences?.Edit()?.PutFloat(ZoomKey, normalized)?.Commit();
#else
            Preferences.Default.Set(ZoomKey, normalized);
#endif
        }
    }

    public string CameraId
    {
        get => Preferences.Default.Get(CameraKey, "rear");
        set => Preferences.Default.Set(CameraKey, string.IsNullOrWhiteSpace(value) ? "rear" : value);
    }

    public int RecognitionFramesPerSecond
    {
        get => NormalizeRecognitionFramesPerSecond(
            Preferences.Default.Get(RecognitionFramesPerSecondKey, DefaultRecognitionFramesPerSecond));
        set => Preferences.Default.Set(
            RecognitionFramesPerSecondKey,
            NormalizeRecognitionFramesPerSecond(value));
    }

    public bool TrackingDiagnosticsEnabled
    {
        get => Preferences.Default.Get(RecognitionDebugKey, false);
        set => Preferences.Default.Set(RecognitionDebugKey, value);
    }

    public bool RecognitionStatisticsEnabled
    {
        get
        {
            if (!Preferences.Default.ContainsKey(RecognitionStatisticsKey))
            {
                Preferences.Default.Set(
                    RecognitionStatisticsKey,
                    Preferences.Default.Get(RecognitionDebugKey, false));
            }

            return Preferences.Default.Get(RecognitionStatisticsKey, false);
        }
        set => Preferences.Default.Set(RecognitionStatisticsKey, value);
    }

    public bool ShowDriveEventLog
    {
        get => Preferences.Default.Get(ShowDriveEventLogKey, false);
        set => Preferences.Default.Set(ShowDriveEventLogKey, value);
    }

    public bool ContinueScanningInBackground
    {
        get => Preferences.Default.Get(ContinueScanningInBackgroundKey, false);
        set => Preferences.Default.Set(ContinueScanningInBackgroundKey, value);
    }

    public string NetworkStreamUrl
    {
        get => _networkStreamUrl;
        set => _networkStreamUrl = value?.Trim() ?? string.Empty;
    }

    public DriveInputConfiguration InputConfiguration
    {
        get
        {
            lock (_inputConfigurationGate)
            {
                return _inputConfiguration ??= ReadInputConfiguration();
            }
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_inputConfigurationGate)
            {
                _inputConfiguration = value;
                var json = JsonSerializer.Serialize(value);
#if ANDROID
                // This contains the per-source zoom and selected source. Persist it synchronously:
                // Android can stop the process immediately after the setup screen loses focus.
                var context = global::Android.App.Application.Context;
                var preferences = context.GetSharedPreferences(
                    $"{context.PackageName}_preferences",
                    global::Android.Content.FileCreationMode.Private);
                preferences?.Edit()?.PutString(InputConfigurationKey, json)?.Commit();
#else
                Preferences.Default.Set(InputConfigurationKey, json);
#endif
            }
        }
    }

    private static DriveInputConfiguration ReadInputConfiguration()
    {
        var json = Preferences.Default.Get(InputConfigurationKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var value = JsonSerializer.Deserialize<DriveInputConfiguration>(json);
                if (value is { Version: DriveInputConfiguration.CurrentVersion, Sources.Count: > 0 })
                {
                    return value;
                }
            }
            catch (JsonException)
            {
            }
        }

        return DriveInputConfiguration.Default;
    }

    private static int NormalizeRecognitionFramesPerSecond(int value) => value switch
    {
        0 or 2 or 4 or 8 or 12 => value,
        _ => DefaultRecognitionFramesPerSecond
    };
}
