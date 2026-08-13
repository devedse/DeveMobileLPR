namespace DeveMobileLPR.App.Controls;

internal sealed class DualCameraPreview : View
{
    public static readonly BindableProperty CameraIdsProperty = BindableProperty.Create(
        nameof(CameraIds), typeof(string), typeof(DualCameraPreview), "2,4");

    public static readonly BindableProperty RequestedWidthProperty = BindableProperty.Create(
        nameof(RequestedWidth), typeof(int), typeof(DualCameraPreview), 1920);

    public static readonly BindableProperty RequestedHeightProperty = BindableProperty.Create(
        nameof(RequestedHeight), typeof(int), typeof(DualCameraPreview), 1080);

    public static readonly BindableProperty IsActiveProperty = BindableProperty.Create(
        nameof(IsActive), typeof(bool), typeof(DualCameraPreview), false);

    public event EventHandler<string>? StatusChanged;

    public string CameraIds
    {
        get => (string)GetValue(CameraIdsProperty);
        set => SetValue(CameraIdsProperty, value);
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

    public void Start(IReadOnlyList<string> cameraIds, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cameraIds.Count, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cameraIds.Count, 4);

        IsActive = false;
        CameraIds = string.Join(',', cameraIds);
        RequestedWidth = width;
        RequestedHeight = height;
        IsActive = true;
    }

    internal string[] GetCameraIds() =>
        CameraIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void Stop() => IsActive = false;

    internal void ReportStatus(string status) =>
        MainThread.BeginInvokeOnMainThread(() => StatusChanged?.Invoke(this, status));
}
