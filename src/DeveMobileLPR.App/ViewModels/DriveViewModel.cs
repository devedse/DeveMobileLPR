using System.Collections.ObjectModel;
using System.ComponentModel;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;
using DeveMobileLPR.App.UI;

namespace DeveMobileLPR.App.ViewModels;

internal sealed class DriveViewModel : ViewModelBase, IDisposable
{
    private readonly DriveCoordinator _coordinator;
    private readonly AppSettings _settings;
    private readonly IBackgroundScanningManager _backgroundScanning;
    private readonly InferenceBackendStatus _backendStatus;
    private readonly Dictionary<string, string> _cameraIds = new(StringComparer.Ordinal);
    private readonly Timer _durationTimer;
    private DriveSnapshot _snapshot;
    private string? _selectedCamera;
    private string _networkStreamUrl;
    private double _zoom;

    public DriveViewModel(
        DriveCoordinator coordinator,
        AppSettings settings,
        IBackgroundScanningManager backgroundScanning,
        InferenceBackendStatus backendStatus)
    {
        _coordinator = coordinator;
        _settings = settings;
        _backgroundScanning = backgroundScanning;
        _backendStatus = backendStatus;
        _snapshot = coordinator.Snapshot;
        _networkStreamUrl = settings.NetworkStreamUrl;
        _zoom = settings.Zoom;
        ToggleDriveCommand = new AsyncCommand(ToggleDriveAsync);
        _coordinator.SnapshotChanged += SnapshotChanged;
        _backendStatus.PropertyChanged += BackendStatusChanged;
        _durationTimer = new Timer(_ => MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(Duration))), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        ApplySnapshot(_snapshot);
    }

    public event EventHandler<bool>? DriveModeChanged;
    public AsyncCommand ToggleDriveCommand { get; }
    public ObservableCollection<string> CameraChoices { get; } = [];
    public bool IsInitializing => _snapshot.IsInitializing;
    public bool IsReady => _snapshot.IsReady;
    public bool IsDriving => _snapshot.IsDriving;
    public bool IsStopping => _snapshot.IsStopping;
    public bool ShowStartPanel => !IsDriving && !IsStopping;
    public bool ShowDriveControls => IsDriving || IsStopping;
    public bool CanStart => IsReady && !IsInitializing && _snapshot.IsInputReady;
    public bool ShowNetworkStreamUrl => _snapshot.SupportsNetworkStreams
        && _snapshot.SelectedCameraId == DriveInputIds.NetworkLlHls;
    public bool IsNetworkStreamPreview => _snapshot.SelectedCameraId == DriveInputIds.NetworkLlHls;
    public IReadOnlyList<DriveOverlay> Overlays => DriveOverlayLayout.GetVisibleOverlays(_snapshot);
    public bool ShowRoadGuide => _snapshot.IsDriving && _snapshot.ShowRoadGuide;
    public string Status => _snapshot.Status;
    public Color StatusColor => _snapshot.HasError ? Color.FromArgb("#FF8D8D") : Color.FromArgb("#E8EDF5");
    public string StatusLabel => _snapshot.HasError ? "Attention" : IsDriving ? "Live" : IsReady ? "Ready" : "Loading";
    public Color StatusAccent => _snapshot.HasError ? Color.FromArgb("#FF6B6B") : IsDriving ? Color.FromArgb("#58E0C2") : Color.FromArgb("#F5C542");
    public string StartButtonText => IsInitializing ? "Preparing…" : "Start drive";
    public string Duration => _snapshot.StartedAt is null ? "0:00" : FormatClock(DateTimeOffset.UtcNow - _snapshot.StartedAt.Value);
    public DriveDiagnosticsSnapshot Diagnostics => _snapshot.Diagnostics;
    public bool ShowRecognitionStatistics => ShowDriveControls && _snapshot.RecognitionStatisticsEnabled;
    public string UniqueVehicles => _snapshot.UniqueVehicles.ToString();
    public string LocationState => _snapshot.HasLocation ? "GPS active" : _settings.TrackLocation ? "Finding GPS" : "Location off";
    public bool HasLatest => _snapshot.RecentSightings.Count > 0;
    public string LatestPlate => HasLatest ? _snapshot.RecentSightings[0].DisplayPlate : "Looking for a plate";
    public string LatestVehicle => HasLatest
        ? string.Join(' ', new[] { _snapshot.RecentSightings[0].Vehicle?.Make, _snapshot.RecentSightings[0].Vehicle?.Model }.Where(value => !string.IsNullOrWhiteSpace(value))) is { Length: > 0 } vehicle ? vehicle : "Confirmed vehicle"
        : "Keep the road area inside the guide";
    public string LatestPrice => HasLatest ? DisplayFormat.Price(_snapshot.RecentSightings[0].Vehicle?.CatalogPrice) : "—";
    public string TopValue => _snapshot.MostExpensive is null ? "—" : DisplayFormat.CompactPrice(_snapshot.MostExpensive.Vehicle?.CatalogPrice);
    public string InferenceBackendSummary => _backendStatus.Summary;

    public double Zoom
    {
        get => _zoom;
        set
        {
            if (SetProperty(ref _zoom, value))
            {
                _coordinator.SetZoom((float)value);
                OnPropertyChanged(nameof(ZoomLabel));
            }
        }
    }

    public string ZoomLabel => $"{Zoom:0.0}×";

    public string NetworkStreamUrl
    {
        get => _networkStreamUrl;
        set
        {
            if (SetProperty(ref _networkStreamUrl, value))
            {
                _coordinator.SetNetworkStreamUrl(value);
            }
        }
    }

    public string? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            if (SetProperty(ref _selectedCamera, value) && value is not null && _cameraIds.TryGetValue(value, out var id))
            {
                _coordinator.SelectCamera(id);
            }
        }
    }

    public Task InitializeAsync() => _coordinator.InitializeAsync();

    private async Task ToggleDriveAsync()
    {
        if (IsDriving)
        {
            await _coordinator.StopDriveAsync();
            _backgroundScanning.Stop();
            return;
        }

        var keepRunning = _backgroundScanning.IsSupported && _settings.ContinueScanningInBackground;
        if (keepRunning)
        {
            if (!_backgroundScanning.HasRequiredPermissions
                && !await _backgroundScanning.RequestPermissionsAsync())
            {
                throw new UnauthorizedAccessException(
                    "Camera access is required to continue recognition in the background.");
            }
            _backgroundScanning.Start();
        }

        try
        {
            await _coordinator.StartDriveAsync();
            if (!_coordinator.Snapshot.IsDriving)
            {
                _backgroundScanning.Stop();
            }
            else if (keepRunning)
            {
                _backgroundScanning.Start();
            }
        }
        catch
        {
            _backgroundScanning.Stop();
            throw;
        }
    }

    private void SnapshotChanged(object? sender, DriveSnapshot snapshot)
    {
        var wasDriving = IsDriving;
        _snapshot = snapshot;
        ApplySnapshot(snapshot);
        if (wasDriving != IsDriving)
        {
            DriveModeChanged?.Invoke(this, IsDriving);
        }
    }

    private void ApplySnapshot(DriveSnapshot snapshot)
    {
        foreach (var property in new[]
        {
            nameof(IsInitializing), nameof(IsReady), nameof(IsDriving), nameof(IsStopping), nameof(ShowStartPanel), nameof(ShowDriveControls),
            nameof(CanStart), nameof(ShowNetworkStreamUrl), nameof(Status), nameof(StatusColor), nameof(StatusLabel), nameof(StatusAccent), nameof(StartButtonText), nameof(Duration),
            nameof(Diagnostics), nameof(ShowRecognitionStatistics),
            nameof(Overlays), nameof(ShowRoadGuide), nameof(IsNetworkStreamPreview),
            nameof(UniqueVehicles), nameof(LocationState), nameof(HasLatest), nameof(LatestPlate),
            nameof(LatestVehicle), nameof(LatestPrice), nameof(TopValue)
        }) OnPropertyChanged(property);
        ToggleDriveCommand.RaiseCanExecuteChanged();

        var selectedName = CameraChoices.FirstOrDefault(name => _cameraIds.TryGetValue(name, out var id) && id == snapshot.SelectedCameraId);
        var desiredNames = snapshot.CameraChoices.Select(choice => choice.Name).ToArray();
        if (!CameraChoices.SequenceEqual(desiredNames))
        {
            CameraChoices.Clear();
            _cameraIds.Clear();
            foreach (var choice in snapshot.CameraChoices)
            {
                CameraChoices.Add(choice.Name);
                _cameraIds[choice.Name] = choice.Id;
            }
            selectedName = snapshot.CameraChoices.FirstOrDefault(choice => choice.Id == snapshot.SelectedCameraId)?.Name ?? CameraChoices.FirstOrDefault();
        }
        if (_selectedCamera != selectedName)
        {
            _selectedCamera = selectedName;
            OnPropertyChanged(nameof(SelectedCamera));
        }
    }

    private static string FormatClock(TimeSpan value) => value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{(int)value.TotalMinutes}:{value.Seconds:00}";

    private void BackendStatusChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(InferenceBackendStatus.Summary))
        {
            OnPropertyChanged(nameof(InferenceBackendSummary));
        }
    }

    public void Dispose()
    {
        _coordinator.SnapshotChanged -= SnapshotChanged;
        _backendStatus.PropertyChanged -= BackendStatusChanged;
        _durationTimer.Dispose();
    }
}
