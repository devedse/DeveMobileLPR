namespace DeveMobileLPR.AndroidApp.Services;

internal sealed class AppSettings
{
    private const string TrackLocationKey = "track_location";
    private const string ShowGuideKey = "show_road_guide";
    private const string HapticKey = "confirmation_haptic";
    private const string ZoomKey = "camera_zoom";
    private const string CameraKey = "camera_id";

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
        set => Preferences.Default.Set(CameraKey, value is "front" ? "front" : "rear");
    }
}
