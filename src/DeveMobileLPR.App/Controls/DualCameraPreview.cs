namespace DeveMobileLPR.App.Controls;

internal sealed class DualCameraPreview : View
{
    public static readonly BindableProperty PrimaryCameraIdProperty = BindableProperty.Create(
        nameof(PrimaryCameraId), typeof(string), typeof(DualCameraPreview), "2");

    public static readonly BindableProperty SecondaryCameraIdProperty = BindableProperty.Create(
        nameof(SecondaryCameraId), typeof(string), typeof(DualCameraPreview), "4");

    public static readonly BindableProperty RequestedWidthProperty = BindableProperty.Create(
        nameof(RequestedWidth), typeof(int), typeof(DualCameraPreview), 1920);

    public static readonly BindableProperty RequestedHeightProperty = BindableProperty.Create(
        nameof(RequestedHeight), typeof(int), typeof(DualCameraPreview), 1080);

    public static readonly BindableProperty IsActiveProperty = BindableProperty.Create(
        nameof(IsActive), typeof(bool), typeof(DualCameraPreview), false);

    public event EventHandler<string>? StatusChanged;

    public string PrimaryCameraId
    {
        get => (string)GetValue(PrimaryCameraIdProperty);
        set => SetValue(PrimaryCameraIdProperty, value);
    }

    public string SecondaryCameraId
    {
        get => (string)GetValue(SecondaryCameraIdProperty);
        set => SetValue(SecondaryCameraIdProperty, value);
    }

    public int RequestedWidth
    {
        get => (int)GetValue(RequestedWidthProperty);
        set => SetValue(RequestedWidthProperty, value);
    }

    public int RequestedHeight
    {
        get => (int)GetValue(RequestedHeightProperty);
        set => SetValue(RequestedHeightProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public void Start(string primaryCameraId, string secondaryCameraId, int width, int height)
    {
        IsActive = false;
        PrimaryCameraId = primaryCameraId;
        SecondaryCameraId = secondaryCameraId;
        RequestedWidth = width;
        RequestedHeight = height;
        IsActive = true;
    }

    public void Stop() => IsActive = false;

    internal void ReportStatus(string status) =>
        MainThread.BeginInvokeOnMainThread(() => StatusChanged?.Invoke(this, status));
}
