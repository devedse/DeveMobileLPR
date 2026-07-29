namespace DeveMobileLPR.App.Services;

internal sealed class AppSettings
{
    private const string TrackLocationKey = "track_location";
    private const string ShowGuideKey = "show_road_guide";
    private const string HapticKey = "confirmation_haptic";
    private const string ZoomKey = "camera_zoom";
    private const string CameraKey = "camera_id";
    private const string RecognitionFramesPerSecondKey = "recognition_frames_per_second";
    private const string RecognitionDebugKey = "recognition_debug";
    private const int DefaultRecognitionFramesPerSecond = 4;
    private string _networkStreamUrl = string.Empty;

    public bool TrackLocation
    {
        get => Preferences.Default.Get(TrackLocationKey, true);
        set => Preferences.Default.Set(TrackLocationKey, value);
    }

    public bool ShowRoadGuide
    {
        get => Preferences.Default.Get(ShowGuideKey, true);
        set => Preferences.Default.Set(ShowGuideKey, value);
    }

    public bool ConfirmationHaptic
    {
        get => Preferences.Default.Get(HapticKey, true);
        set => Preferences.Default.Set(HapticKey, value);
    }

    public float Zoom
    {
        get => Math.Clamp(Preferences.Default.Get(ZoomKey, 1f), 1f, 4f);
        set => Preferences.Default.Set(ZoomKey, Math.Clamp(value, 1f, 4f));
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

    public bool RecognitionDebugEnabled
    {
        get => Preferences.Default.Get(RecognitionDebugKey, false);
        set => Preferences.Default.Set(RecognitionDebugKey, value);
    }

    public string NetworkStreamUrl
    {
        get => _networkStreamUrl;
        set => _networkStreamUrl = value?.Trim() ?? string.Empty;
    }

    private static int NormalizeRecognitionFramesPerSecond(int value) => value switch
    {
        0 or 2 or 4 or 8 or 12 => value,
        _ => DefaultRecognitionFramesPerSecond
    };
}
