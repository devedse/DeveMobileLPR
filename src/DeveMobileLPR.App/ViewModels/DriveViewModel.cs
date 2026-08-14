using System.Collections.ObjectModel;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;
using DeveMobileLPR.App.UI;

namespace DeveMobileLPR.App.ViewModels;

internal sealed class DriveViewModel : ViewModelBase, IDisposable
{
    private readonly DriveCoordinator _coordinator;
    private readonly AppSettings _settings;
    private readonly IDriveSourceCatalog _sourceCatalog;
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

    public DriveViewModel(
        DriveCoordinator coordinator,
        AppSettings settings,
        IDriveSourceCatalog sourceCatalog,
        IBackgroundScanningManager backgroundScanning)
    {
        _coordinator = coordinator;
        _settings = settings;
        _sourceCatalog = sourceCatalog;
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
    public bool ShowDriveControls => IsDriving || IsStopping;
    public bool CanStart => IsReady && !IsInitializing && IsInputConfigurationValid;
    public bool SupportsMultiCamera => _sourceCatalog.SupportsMultipleSources;
    public bool ShowNetworkStreamUrl => _snapshot.SupportsNetworkStreams
        && _snapshot.SelectedCameraId == DriveInputIds.NetworkLlHls;
    public bool IsNetworkStreamPreview => _snapshot.SelectedCameraId == DriveInputIds.NetworkLlHls;
    public IReadOnlyList<DriveOverlay> Overlays => DriveOverlayLayout.GetVisibleOverlays(_snapshot);
    public IReadOnlyList<string> OverlaySourceIds => _snapshot.ActiveSourceIds ?? [];
    public bool ShowRoadGuide => !IsMultiCamera && _snapshot.IsDriving && _snapshot.ShowRoadGuide;
    public string Status => _snapshot.Status;
    public string EventLog => string.Join(Environment.NewLine, (_snapshot.EventLog ?? []).TakeLast(6));
    public bool HasEventLog => _snapshot.ShowDriveEventLog && _snapshot.EventLog is { Count: > 0 };
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
                _zoom = value.Zoom;
                _settings.Zoom = (float)_zoom;
                OnPropertyChanged(nameof(Zoom));
                OnPropertyChanged(nameof(ZoomLabel));
                OnPropertyChanged(nameof(ActiveZoomMinimum));
                OnPropertyChanged(nameof(ActiveZoomMaximum));
                OnPropertyChanged(nameof(ActiveCameraName));
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
        $"This device supports at most {_sourceCatalog.MaximumSimultaneousIntegratedSources} " +
        "simultaneous integrated camera streams. LL-HLS does not consume an integrated-camera slot.";
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
                SelectedSingleSource?.SetZoomFromActiveCamera(value);
                _coordinator.SetZoom((float)value);
                OnPropertyChanged(nameof(ZoomLabel));
            }
        }
    }

    public string ZoomLabel => $"{Zoom:0.0}×";
    public double ActiveZoomMinimum => SelectedSingleSource?.MinimumZoom ?? 1d;
    public double ActiveZoomMaximum => SelectedSingleSource?.MaximumZoom ?? 5d;
    public string ActiveCameraName => SelectedSingleSource?.Name ?? _selectedCamera ?? "Camera";

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
        if (SingleSources.Count > 0)
        {
            return;
        }

        var capabilities = await _sourceCatalog.DiscoverAsync();
        if (capabilities.Count == 0)
        {
            InputConfigurationError = "No video sources are available.";
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

        foreach (var capability in capabilities
                     .Where(source => source.Kind is DriveSourceKind.LogicalCamera or DriveSourceKind.NetworkLlHls))
        {
            SingleSources.Add(Create(capability));
        }
        foreach (var capability in capabilities
                     .Where(source => source.Kind == DriveSourceKind.PhysicalCamera
                         || source.Id == "front"
                         || source.Kind == DriveSourceKind.NetworkLlHls))
        {
            MultiSources.Add(Create(capability));
        }

        _isMultiCamera = SupportsMultiCamera && configuration.Mode == DriveInputMode.Multi;
        _selectedSingleSource = SingleSources.FirstOrDefault(source =>
                source.Id == (configuration.SelectedSingleSourceId ?? "rear"))
            ?? SingleSources.FirstOrDefault();
        if (_selectedSingleSource is not null)
        {
            _zoom = _selectedSingleSource.Zoom;
            _settings.Zoom = (float)_zoom;
        }
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
        if (IsMultiCamera && !SupportsMultiCamera)
        {
            return "Multiple simultaneous sources are not supported on this platform.";
        }
        if (selected.Count(source => source.Capability.IsIntegratedCamera)
            > _sourceCatalog.MaximumSimultaneousIntegratedSources)
        {
            return $"Select at most {_sourceCatalog.MaximumSimultaneousIntegratedSources} integrated camera sources.";
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

        var multiSourceIds = MultiSources.Select(source => source.Id).ToHashSet(StringComparer.Ordinal);
        var allProfiles = SingleSources
            .Concat(MultiSources)
            .DistinctBy(source => source.Id)
            .Select(source =>
            {
                var profile = source.ToProfile(IsMultiCamera);
                return IsMultiCamera && !multiSourceIds.Contains(source.Id)
                    ? profile with { Enabled = false }
                    : profile;
            })
            .ToArray();
        var configuration = new DriveInputConfiguration(
            DriveInputConfiguration.CurrentVersion,
            IsMultiCamera ? DriveInputMode.Multi : DriveInputMode.Single,
            allProfiles,
            SelectedSingleSource?.Id ?? "rear");
        _settings.InputConfiguration = configuration;
        if (!IsMultiCamera && SelectedSingleSource is { IsIntegratedCamera: true } selectedSingle)
        {
            _zoom = selectedSingle.Zoom;
            _settings.Zoom = (float)_zoom;
            OnPropertyChanged(nameof(Zoom));
            OnPropertyChanged(nameof(ZoomLabel));
            if (IsDriving)
            {
                _coordinator.SetZoom((float)_zoom);
            }
        }
        InputConfigurationError = GetInputConfigurationError();
        OnPropertyChanged(nameof(IsInputConfigurationValid));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(MultiCameraWarning));
        ToggleDriveCommand.RaiseCanExecuteChanged();

    }

    public async Task StartDriveAsync()
    {
        if (IsDriving)
        {
            return;
        }

        // The setup page deliberately has no camera view. When the live modal opens, MAUI can
        // invoke OnAppearing before CameraPreviewHandler has created and attached its platform
        // camera. Wait for that asynchronous attachment/configuration instead of treating the
        // normal page-construction race as a camera failure.
        if (!_coordinator.Snapshot.IsInputReady
            && !await WaitForInputReadyAsync(TimeSpan.FromSeconds(15)))
        {
            return;
        }

        await ToggleDriveAsync();
    }

    private async Task<bool> WaitForInputReadyAsync(TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void Observe(object? sender, DriveSnapshot snapshot)
        {
            if (snapshot.IsInputReady)
            {
                completion.TrySetResult(true);
            }
        }

        _coordinator.SnapshotChanged += Observe;
        try
        {
            var snapshot = _coordinator.Snapshot;
            if (snapshot.IsInputReady)
            {
                return true;
            }
            return await completion.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            _coordinator.SnapshotChanged -= Observe;
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
                // Start the foreground service only after camera startup succeeds. Starting it
                // before the drive can leave Android waiting for StartForeground when startup is
                // rejected, which Android reports later as a fatal process exception.
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
            nameof(IsInitializing), nameof(IsReady), nameof(IsDriving), nameof(IsStopping), nameof(ShowDriveControls),
            nameof(CanStart), nameof(ShowNetworkStreamUrl), nameof(Status), nameof(EventLog), nameof(HasEventLog), nameof(StatusColor), nameof(StatusLabel), nameof(StatusAccent), nameof(StartButtonText), nameof(Duration),
            nameof(Diagnostics), nameof(ShowRecognitionStatistics),
            nameof(Overlays), nameof(OverlaySourceIds), nameof(ShowRoadGuide), nameof(IsNetworkStreamPreview),
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
            OnPropertyChanged(nameof(ActiveCameraName));
        }
    }

    private static string FormatClock(TimeSpan value) => value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    public void Dispose()
    {
        _coordinator.SnapshotChanged -= SnapshotChanged;
        _durationTimer.Dispose();
    }
}
