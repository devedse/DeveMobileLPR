using DeveMobileLPR.Application;

namespace DeveMobileLPR.App.ViewModels;

internal sealed class DriveSourceOptionViewModel : ViewModelBase
{
    private readonly Action _changed;
    private bool _isEnabled;
    private VideoResolution _selectedResolution;
    private double _zoom;
    private string _networkUrl;

    public DriveSourceOptionViewModel(
        DriveSourceCapability capability,
        DriveSourceProfile profile,
        Action changed)
    {
        Capability = capability;
        _changed = changed;
        Resolutions = capability.Resolutions.Count > 0
            ? capability.Resolutions
            : [profile.Resolution];
        _selectedResolution = Resolutions.FirstOrDefault(size => size == profile.Resolution)
            ?? Resolutions[0];
        _isEnabled = profile.Enabled;
        _zoom = Math.Clamp(profile.Zoom, capability.MinimumZoom, capability.MaximumZoom);
        _networkUrl = profile.NetworkUrl ?? string.Empty;
    }

    public DriveSourceCapability Capability { get; }
    public string Id => Capability.Id;
    public string Name => Capability.Name;
    public string Metadata => Capability.Kind == DriveSourceKind.NetworkLlHls
        ? "Network source · does not consume an integrated-camera slot"
        : string.Join(
            " · ",
            new[]
            {
                Capability.PhysicalCameraId is null ? null : $"physical {Capability.PhysicalCameraId}",
                Capability.FocalLengthMillimeters is { } focal ? $"{focal:0.##} mm" : null,
                Capability.SensorWidthMillimeters is { } width && Capability.SensorHeightMillimeters is { } height
                    ? $"{width:0.##}×{height:0.##} mm sensor"
                    : null
            }.Where(value => value is not null));
    public bool IsNetwork => Capability.Kind == DriveSourceKind.NetworkLlHls;
    public bool IsIntegratedCamera => !IsNetwork;
    public IReadOnlyList<VideoResolution> Resolutions { get; }
    public double MinimumZoom => Capability.MinimumZoom;
    public double MaximumZoom => Capability.MaximumZoom;
    public string ZoomLabel => $"{Zoom:0.0}×";

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                _changed();
            }
        }
    }

    public VideoResolution SelectedResolution
    {
        get => _selectedResolution;
        set
        {
            if (value is not null && SetProperty(ref _selectedResolution, value))
            {
                _changed();
            }
        }
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            var normalized = Math.Clamp(value, MinimumZoom, MaximumZoom);
            if (SetProperty(ref _zoom, normalized))
            {
                OnPropertyChanged(nameof(ZoomLabel));
                _changed();
            }
        }
    }

    public string NetworkUrl
    {
        get => _networkUrl;
        set
        {
            if (SetProperty(ref _networkUrl, value ?? string.Empty))
            {
                _changed();
            }
        }
    }

    public DriveSourceProfile ToProfile() =>
        new(Id, IsEnabled, SelectedResolution, (float)Zoom, IsNetwork ? NetworkUrl.Trim() : null);
}
