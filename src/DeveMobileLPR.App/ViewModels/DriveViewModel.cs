using System.Collections.ObjectModel;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;
using DeveMobileLPR.App.UI;

namespace DeveMobileLPR.App.ViewModels;

internal sealed class DriveViewModel : ViewModelBase, IDisposable
{
    private readonly DriveCoordinator _coordinator;
    private readonly AppSettings _settings;
    private readonly IBackgroundScanningManager _backgroundScanning;
    private readonly Dictionary<string, string> _cameraIds = new(StringComparer.Ordinal);
    private readonly Timer _durationTimer;
    private DriveSnapshot _snapshot;
    private string? _selectedCamera;
    private string _networkStreamUrl;
    private double _zoom;
    private bool _isMultiCamera;
    private DriveSourceOptionViewModel? _selectedSingleSource;
    private string _inputConfigurationError = string.Empty;
    private CancellationTokenSource? _inputConfigurationCancellation;

    public DriveViewModel(
        DriveCoordinator coordinator,
        AppSettings settings,
        IBackgroundScanningManager backgroundScanning)
    {
        _coordinator = coordinator;
        _settings = settings;
        _backgroundScanning = backgroundScanning;
        _snapshot = coordinator.Snapshot;
        _networkStreamUrl = settings.NetworkStreamUrl;
        _zoom = settings.Zoom;
        ToggleDriveCommand = new AsyncCommand(ToggleDriveAsync);
        _coordinator.SnapshotChanged += SnapshotChanged;
        _durationTimer = new Timer(_ => MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(Duration))), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        ApplySnapshot(_snapshot);
    }

    public event EventHandler<bool>? DriveModeChanged;
    public AsyncCommand ToggleDriveCommand { get; }
    public ObservableCollection<string> CameraChoices { get; } = [];
    public ObservableCollection<DriveSourceOptionViewModel> SingleSources { get; } = [];
    public ObservableCollection<DriveSourceOptionViewModel> MultiSources { get; } = [];
    public bool IsInitializing => _snapshot.IsInitializing;
    public bool IsReady => _snapshot.IsReady;
    public bool IsDriving => _snapshot.IsDriving;
    public bool IsStopping => _snapshot.IsStopping;
    public bool ShowStartPanel => !IsDriving && !IsStopping;
    public bool ShowDriveControls => IsDriving || IsStopping;
    public bool CanStart => IsReady && !IsInitializing && _snapshot.IsInputReady && IsInputConfigurationValid;
    public bool ShowNetworkStreamUrl => _snapshot.SupportsNetworkStreams
        && _snapshot.SelectedCameraId == DriveInputIds.NetworkLlHls;
    public bool IsNetworkStreamPreview => _snapshot.SelectedCameraId == DriveInputIds.NetworkLlHls;
    public IReadOnlyList<DriveOverlay> Overlays => IsMultiCamera
        ? []
        : DriveOverlayLayout.GetVisibleOverlays(_snapshot);
    public bool ShowRoadGuide => !IsMultiCamera && _snapshot.IsDriving && _snapshot.ShowRoadGuide;
    public string Status => _snapshot.Status;
    public string EventLog => string.Join(Environment.NewLine, (_snapshot.EventLog ?? []).TakeLast(6));
    public bool HasEventLog => _snapshot.EventLog is { Count: > 0 };
    public Color StatusColor => _snapshot.HasError ? Color.FromArgb("#FF8D8D") : Color.FromArgb("#E8EDF5");
    public string StatusLabel => _snapshot.HasError ? "Attention" : IsDriving ? "Live" : IsReady ? "Ready" : "Loading";
    public Color StatusAccent => _snapshot.HasError ? Color.FromArgb("#FF6B6B") : IsDriving ? Color.FromArgb("#58E0C2") : Color.FromArgb("#F5C542");
    public bool IsMultiCamera
    {
        get => _isMultiCamera;
        set
        {
            if (SetProperty(ref _isMultiCamera, value))
            {
                OnPropertyChanged(nameof(IsSingleCamera));
                OnPropertyChanged(nameof(MultiCameraWarning));
                QueueInputConfigurationUpdate();
            }
        }
    }

    public bool IsSingleCamera => !IsMultiCamera;

    public DriveSourceOptionViewModel? SelectedSingleSource
    {
        get => _selectedSingleSource;
        set
        {
            if (SetProperty(ref _selectedSingleSource, value) && value is not null)
            {
                OnPropertyChanged(nameof(SingleSourceIsNetwork));
                QueueInputConfigurationUpdate();
            }
        }
    }

    public bool SingleSourceIsNetwork => SelectedSingleSource?.IsNetwork == true;
    public string InputConfigurationError
    {
        get => _inputConfigurationError;
        private set
        {
            if (SetProperty(ref _inputConfigurationError, value))
            {
                OnPropertyChanged(nameof(HasInputConfigurationError));
                OnPropertyChanged(nameof(IsInputConfigurationValid));
                OnPropertyChanged(nameof(CanStart));
                ToggleDriveCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public bool HasInputConfigurationError => !string.IsNullOrWhiteSpace(InputConfigurationError);
    public bool IsInputConfigurationValid => string.IsNullOrEmpty(GetInputConfigurationError());
    public string MultiCameraWarning =>
        "Most Android phones support at most two integrated cameras simultaneously. " +
        "LL-HLS does not count as an integrated camera.";
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

    public async Task InitializeAsync()
    {
        await _coordinator.InitializeAsync();
        await InitializeInputSourcesAsync();
    }

    private async Task InitializeInputSourcesAsync()
    {
        for (var attempt = 0; attempt < 20 && _coordinator.SourceCapabilities.Count == 0; attempt++)
        {
            await Task.Delay(100);
        }

        if (SingleSources.Count > 0 || _coordinator.SourceCapabilities.Count == 0)
        {
            return;
        }

        var configuration = _settings.InputConfiguration;
        var profiles = configuration.Sources.ToDictionary(profile => profile.SourceId, StringComparer.Ordinal);
        var optionsById = new Dictionary<string, DriveSourceOptionViewModel>(StringComparer.Ordinal);

        DriveSourceOptionViewModel Create(DriveSourceCapability capability)
        {
            if (optionsById.TryGetValue(capability.Id, out var existing))
            {
                return existing;
            }

            if (!profiles.TryGetValue(capability.Id, out var profile))
            {
                var requested = capability.Resolutions.FirstOrDefault(size => size is { Width: 3840, Height: 2160 })
                    ?? capability.Resolutions.FirstOrDefault()
                    ?? new VideoResolution(3840, 2160);
                profile = new DriveSourceProfile(
                    capability.Id,
                    false,
                    requested,
                    Math.Max(1f, capability.MinimumZoom),
                    capability.Kind == DriveSourceKind.NetworkLlHls ? _settings.NetworkStreamUrl : null);
            }

            var option = new DriveSourceOptionViewModel(capability, profile, QueueInputConfigurationUpdate);
            optionsById.Add(capability.Id, option);
            return option;
        }

        foreach (var capability in _coordinator.SourceCapabilities
                     .Where(source => source.Kind is DriveSourceKind.LogicalCamera or DriveSourceKind.NetworkLlHls))
        {
            SingleSources.Add(Create(capability));
        }
        foreach (var capability in _coordinator.SourceCapabilities
                     .Where(source => source.Kind == DriveSourceKind.PhysicalCamera
                         || source.Id == "front"
                         || source.Kind == DriveSourceKind.NetworkLlHls))
        {
            MultiSources.Add(Create(capability));
        }

        _isMultiCamera = configuration.Mode == DriveInputMode.Multi;
        _selectedSingleSource = SingleSources.FirstOrDefault(source =>
                source.Id == (configuration.SelectedSingleSourceId ?? "rear"))
            ?? SingleSources.FirstOrDefault();
        InputConfigurationError = GetInputConfigurationError();
        OnPropertyChanged(nameof(IsMultiCamera));
        OnPropertyChanged(nameof(IsSingleCamera));
        OnPropertyChanged(nameof(SelectedSingleSource));
        OnPropertyChanged(nameof(SingleSourceIsNetwork));
        OnPropertyChanged(nameof(MultiCameraWarning));
        OnPropertyChanged(nameof(IsInputConfigurationValid));
        OnPropertyChanged(nameof(CanStart));
        QueueInputConfigurationUpdate();
    }

    private string GetInputConfigurationError()
    {
        if (SingleSources.Count == 0)
        {
            return "Video sources are still loading.";
        }

        var selected = IsMultiCamera
            ? MultiSources.Where(source => source.IsEnabled).ToArray()
            : SelectedSingleSource is null ? [] : [SelectedSingleSource];
        if (selected.Length == 0)
        {
            return "Select at least one video source.";
        }
        if (selected.Count(source => source.Capability.IsIntegratedCamera) > 2)
        {
            return "This implementation supports at most two integrated cameras at once. You can also add LL-HLS.";
        }
        if (selected.Any(source => source.IsNetwork && !IsValidNetworkUrl(source.NetworkUrl)))
        {
            return "Enter a valid HTTP or HTTPS LL-HLS playlist URL.";
        }

        return string.Empty;
    }

    private static bool IsValidNetworkUrl(string value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void QueueInputConfigurationUpdate()
    {
        if (SingleSources.Count == 0)
        {
            return;
        }

        var allProfiles = SingleSources
            .Concat(MultiSources)
            .DistinctBy(source => source.Id)
            .Select(source => source.ToProfile())
            .ToArray();
        var configuration = new DriveInputConfiguration(
            DriveInputConfiguration.CurrentVersion,
            IsMultiCamera ? DriveInputMode.Multi : DriveInputMode.Single,
            allProfiles,
            SelectedSingleSource?.Id ?? "rear");
        _settings.InputConfiguration = configuration;
        InputConfigurationError = GetInputConfigurationError();
        OnPropertyChanged(nameof(IsInputConfigurationValid));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(MultiCameraWarning));
        ToggleDriveCommand.RaiseCanExecuteChanged();

        _inputConfigurationCancellation?.Cancel();
        _inputConfigurationCancellation?.Dispose();
        if (!IsInputConfigurationValid)
        {
            _inputConfigurationCancellation = null;
            return;
        }

        _inputConfigurationCancellation = new CancellationTokenSource();
        _ = ApplyInputConfigurationAfterDelayAsync(configuration, _inputConfigurationCancellation.Token);
    }

    private async Task ApplyInputConfigurationAfterDelayAsync(
        DriveInputConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(200, cancellationToken);
            await _coordinator.ApplyInputConfigurationAsync(configuration, cancellationToken);
            InputConfigurationError = string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            InputConfigurationError = exception.Message;
        }
    }

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
            nameof(CanStart), nameof(ShowNetworkStreamUrl), nameof(Status), nameof(EventLog), nameof(HasEventLog), nameof(StatusColor), nameof(StatusLabel), nameof(StatusAccent), nameof(StartButtonText), nameof(Duration),
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
    public void Dispose()
    {
        _coordinator.SnapshotChanged -= SnapshotChanged;
        _inputConfigurationCancellation?.Cancel();
        _inputConfigurationCancellation?.Dispose();
        _durationTimer.Dispose();
    }
}
