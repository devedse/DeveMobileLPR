using DeveMobileLPR.Imaging;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application;

public sealed class DriveCoordinator : IAsyncDisposable
{
    private readonly ISightingRepository _repository;
    private readonly IVehicleImageStore _vehicleImageStore;
    private readonly IDriveSettings _settings;
    private readonly IVehicleDataStatus _vehicleDataStatus;
    private readonly RecognitionTuningConfiguration _recognitionTuning;
    private readonly IRecognitionPipelineProvider _pipelineProvider;
    private readonly IVehicleLookup _vehicleLookup;
    private readonly IDriveLocationTracker _location;
    private readonly IDeviceExperience _deviceExperience;
    private readonly IApplicationDispatcher _dispatcher;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _driveGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly DrivePerformanceMonitor _performance = new();
    private readonly HashSet<string> _uniqueVehicles = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Sighting> _sessionSightings = [];
    private readonly List<Sighting> _recentSightings = [];
    private readonly ConfirmedOverlayTracker _confirmedPlates = new(() => DateTimeOffset.UtcNow);
    private RecognitionSession? _recognition;
    private IDriveVideoInput? _camera;
    private Task? _cameraInitialization;
    private CancellationTokenSource? _routeCancellation;
    private Task? _routeWorker;
    private GeoPoint? _lastRoutePoint;
    private DateTimeOffset _lastRouteAt;
    private long _activeTripId;
    private bool _initializing;
    private bool _ready;
    private bool _driving;
    private bool _stopping;
    private bool _disposed;
    private string _status = "Preparing the on-device recognition engine…";
    private bool _hasError;
    private DateTimeOffset? _startedAt;
    private DriveDiagnosticsSnapshot _diagnostics = DriveDiagnosticsSnapshot.Empty;
    private Sighting? _mostExpensive;
    private IReadOnlyList<DriveOverlay> _liveOverlays = [];
    private IReadOnlyList<CameraChoice> _cameraChoices = [new("rear", "Rear cameras · automatic lens")];
    public DriveCoordinator(
        ISightingRepository repository,
        IVehicleImageStore vehicleImageStore,
        IDriveSettings settings,
        IVehicleDataStatus vehicleDataStatus,
        RecognitionTuningConfiguration recognitionTuning,
        IRecognitionPipelineProvider pipelineProvider,
        IVehicleLookup vehicleLookup,
        IDriveLocationTracker location,
        IDeviceExperience deviceExperience,
        IApplicationDispatcher dispatcher)
    {
        _repository = repository;
        _vehicleImageStore = vehicleImageStore;
        _settings = settings;
        _vehicleDataStatus = vehicleDataStatus;
        _recognitionTuning = recognitionTuning;
        _pipelineProvider = pipelineProvider;
        _vehicleLookup = vehicleLookup;
        _location = location;
        _deviceExperience = deviceExperience;
        _dispatcher = dispatcher;
        _performance.Sampled += PerformanceSampled;
    }

    public event EventHandler<DriveSnapshot>? SnapshotChanged;
    public ISightingRepository Repository => _repository;
    public IVehicleImageStore VehicleImageStore => _vehicleImageStore;
    public DriveSnapshot Snapshot { get { lock (_stateGate) return CreateSnapshot(); } }
    public long? ActiveTripId { get { var value = Interlocked.Read(ref _activeTripId); return value == 0 ? null : value; } }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ready)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_ready)
            {
                return;
            }

            lock (_stateGate)
            {
                _initializing = true;
                _hasError = false;
                _status = "Opening your private trip library…";
            }
            Publish();
            await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);

            SetStatus("Verifying the bundled plate models…");
            var pipeline = await _pipelineProvider.CreateAsync(SetStatus, cancellationToken).ConfigureAwait(false);
            _recognition = new RecognitionSession(
                pipeline,
                _recognitionTuning,
                _repository,
                _vehicleImageStore,
                _vehicleLookup,
                () => _settings.SaveVehicleImages,
                () => _location.Latest,
                () => ActiveTripId);
            _recognition.Progress += RecognitionProgressed;
            _recognition.PlateConfirmed += PlateConfirmed;
            _recognition.Failed += RecognitionFailed;

            lock (_stateGate)
            {
                _ready = true;
                _initializing = false;
                _status = _vehicleDataStatus.IsAvailable
                    ? "Ready · vehicle details available"
                    : "Ready · import RDW in Settings for vehicle details";
            }
            Publish();
        }
        catch (Exception exception)
        {
            lock (_stateGate)
            {
                _initializing = false;
                _hasError = true;
                _status = $"Could not prepare recognition: {exception.Message}";
            }
            Publish();
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public void AttachCamera(IDriveVideoInput camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        _camera = camera;
        camera.Diagnostic += CameraDiagnostic;
        camera.CameraChoicesChanged += CameraChoicesChanged;
        camera.SourceFramesAvailable += SourceFramesAvailable;
        camera.PreviewFramesPresented += PreviewFramesPresented;
        _cameraChoices = camera.CameraChoices;
        camera.SetNetworkStreamUrl(_settings.NetworkStreamUrl);
        _cameraInitialization = InitializeCameraAsync(camera, _settings.CameraId);
        camera.SetZoom(_settings.Zoom);
        Publish();
    }

    public void DetachCamera(IDriveVideoInput camera)
    {
        camera.Diagnostic -= CameraDiagnostic;
        camera.CameraChoicesChanged -= CameraChoicesChanged;
        camera.SourceFramesAvailable -= SourceFramesAvailable;
        camera.PreviewFramesPresented -= PreviewFramesPresented;
        if (ReferenceEquals(_camera, camera))
        {
            _camera = null;
            _cameraInitialization = null;
        }
    }

    public bool SubmitFrame(Yuv420Frame frame)
    {
        if (!_driving || _recognition is null)
        {
            frame.Dispose();
            return false;
        }

        return _recognition.Submit(frame);
    }

    public async Task StartDriveAsync()
    {
        await _driveGate.WaitAsync();
        IDriveVideoInput? startedCamera = null;
        try
        {
            if (_driving || _stopping)
            {
                return;
            }

            await InitializeAsync();
            if (!_ready)
            {
                return;
            }

            if (_camera is null)
            {
                SetStatus("The camera preview is not available yet.", true);
                return;
            }

            var camera = _camera;
            if (_cameraInitialization is { } cameraInitialization)
            {
                await cameraInitialization.ConfigureAwait(false);
            }
            if (!ReferenceEquals(_camera, camera) || !camera.IsReady)
            {
                SetStatus("The selected video input is not ready yet.", true);
                return;
            }

            if (_settings.TrackLocation)
            {
                await _location.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var now = DateTimeOffset.UtcNow;
            var trip = await _repository.StartTripAsync(now, _location.Latest, CancellationToken.None);
            Interlocked.Exchange(ref _activeTripId, trip.Id);
            _recognition?.ResetTracking();
            lock (_stateGate)
            {
                _driving = true;
                _stopping = false;
                _startedAt = now;
                _diagnostics = DriveDiagnosticsSnapshot.Empty;
                _uniqueVehicles.Clear();
                _sessionSightings.Clear();
                _recentSightings.Clear();
                _mostExpensive = null;
                _liveOverlays = [];
                _confirmedPlates.Clear();
                _hasError = false;
                _status = "Scanning · video stays on this device";
            }

            _performance.Start();
            startedCamera = camera;
            await camera.StartAsync();
            camera.SetZoom(_settings.Zoom);
            if (_location.Latest is { } initial)
            {
                await _repository.AddTripPointAsync(trip.Id, now, initial, CancellationToken.None);
                _lastRoutePoint = initial;
                _lastRouteAt = now;
            }
            _routeCancellation = new CancellationTokenSource();
            _routeWorker = Task.Run(() => RecordRouteAsync(trip.Id, _routeCancellation.Token));
            _deviceExperience.SetKeepScreenOn(true);
            Publish();
        }
        catch (Exception exception)
        {
            _performance.Stop();
            var cleanupFailures = await FinalizeDriveResourcesAsync(startedCamera, waitForInFlightRecognition: false);
            lock (_stateGate)
            {
                _driving = false;
                _stopping = false;
                _startedAt = null;
                _diagnostics = DriveDiagnosticsSnapshot.Empty;
            }
            SetStatus(
                cleanupFailures.Count == 0
                    ? $"Could not start this drive: {exception.Message}"
                    : $"Could not start this drive: {exception.Message} Cleanup also failed: {cleanupFailures[0].Message}",
                true);
        }
        finally
        {
            _driveGate.Release();
        }
    }

    public async Task StopDriveAsync()
    {
        await _driveGate.WaitAsync();
        try
        {
            if (!_driving || _stopping)
            {
                return;
            }

            lock (_stateGate)
            {
                _driving = false;
                _stopping = true;
                _status = "Finishing your trip…";
                _liveOverlays = [];
                _confirmedPlates.Clear();
                _diagnostics = DriveDiagnosticsSnapshot.Empty;
            }
            _performance.Stop();
            Publish();

            var failures = await FinalizeDriveResourcesAsync(_camera, waitForInFlightRecognition: true);

            lock (_stateGate)
            {
                _stopping = false;
                _startedAt = null;
                _hasError = failures.Count > 0;
                _status = failures.Count == 0
                    ? "Trip saved · review it in History"
                    : $"The trip stopped, but could not be finalized: {failures[0].Message}";
            }
            Publish();
        }
        catch (Exception exception)
        {
            lock (_stateGate) _stopping = false;
            SetStatus($"The trip stopped, but could not be finalized: {exception.Message}", true);
        }
        finally
        {
            _driveGate.Release();
        }
    }

    public void SetZoom(float zoom)
    {
        _settings.Zoom = zoom;
        _camera?.SetZoom(_settings.Zoom);
    }

    public void SetNetworkStreamUrl(string value)
    {
        _settings.NetworkStreamUrl = value;
        _camera?.SetNetworkStreamUrl(_settings.NetworkStreamUrl);
        Publish();
    }

    public void SelectCamera(string cameraId)
    {
        if (_camera is null)
        {
            _settings.CameraId = cameraId;
            Publish();
            return;
        }

        _ = SelectCameraAsync(_camera, cameraId);
    }

    private async Task SelectCameraAsync(IDriveVideoInput camera, string cameraId)
    {
        try
        {
            await camera.SelectCameraAsync(cameraId);
            if (!ReferenceEquals(_camera, camera))
            {
                return;
            }
            _settings.CameraId = camera.SelectedCameraId;
            ResetInputPerformance(camera);
            Publish();
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_camera, camera))
            {
                SetStatus($"Could not switch video input: {exception.Message}", true);
            }
        }
    }

    private void ResetInputPerformance(IDriveVideoInput camera)
    {
        _performance.ResetSampleWindow();
        _recognition?.ResetTracking();
        lock (_stateGate)
        {
            _diagnostics = DriveDiagnosticsSnapshot.Empty;
            _liveOverlays = [];
            _confirmedPlates.Clear();
        }
    }

    public void RefreshSettings()
    {
        Publish();
    }

    private async Task InitializeCameraAsync(IDriveVideoInput camera, string preferredCameraId)
    {
        try
        {
            await camera.InitializeAsync(preferredCameraId).ConfigureAwait(false);
            if (!ReferenceEquals(_camera, camera))
            {
                return;
            }

            _settings.CameraId = camera.SelectedCameraId;
            if (_driving && !_stopping)
            {
                await camera.StartAsync().ConfigureAwait(false);
            }
            Publish();
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_camera, camera))
            {
                SetStatus($"Could not initialize video input: {exception.Message}", true);
            }
        }
    }

    private async Task<IReadOnlyList<Exception>> FinalizeDriveResourcesAsync(
        IDriveVideoInput? camera,
        bool waitForInFlightRecognition)
    {
        var failures = new List<Exception>();
        if (camera is not null)
        {
            await CaptureFailureAsync(() => camera.StopAsync(), failures);
        }

        CaptureFailure(() => _routeCancellation?.Cancel(), failures);
        if (_routeWorker is not null)
        {
            try
            {
                await _routeWorker;
            }
            catch (OperationCanceledException) when (_routeCancellation?.IsCancellationRequested == true)
            {
                // Expected when stopping the route recorder.
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (waitForInFlightRecognition)
        {
            // Let the one in-flight inference frame persist against the still-active trip.
            await Task.Delay(350);
        }

        var tripId = ActiveTripId;
        if (tripId is not null)
        {
            await CaptureFailureAsync(
                () => _repository.EndTripAsync(tripId.Value, DateTimeOffset.UtcNow, _location.Latest, CancellationToken.None),
                failures);
        }

        Interlocked.Exchange(ref _activeTripId, 0);
        CaptureFailure(() => _recognition?.ResetTracking(), failures);
        CaptureFailure(_location.Stop, failures);
        CaptureFailure(() => _routeCancellation?.Dispose(), failures);
        _routeCancellation = null;
        _routeWorker = null;
        CaptureFailure(() => _deviceExperience.SetKeepScreenOn(false), failures);
        return failures;
    }

    private static async Task CaptureFailureAsync(Func<Task> action, ICollection<Exception> failures)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void CaptureFailure(Action action, ICollection<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    public async Task DeleteHistoryAsync()
    {
        if (_driving || _stopping)
        {
            throw new InvalidOperationException("Stop the active drive before deleting history.");
        }
        await _repository.DeleteHistoryAsync(CancellationToken.None);
        try
        {
            await _vehicleImageStore.DeleteAllAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("History was deleted, but vehicle image cleanup failed.", exception);
        }
    }

    private async Task RecordRouteAsync(long tripId, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_location.Latest is not { } point || point.AccuracyMeters is > 75)
            {
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            if (_lastRoutePoint is not null && DistanceMeters(_lastRoutePoint.Value, point) < 12 && now - _lastRouteAt < TimeSpan.FromSeconds(30))
            {
                continue;
            }

            await _repository.AddTripPointAsync(tripId, now, point, cancellationToken).ConfigureAwait(false);
            _lastRoutePoint = point;
            _lastRouteAt = now;
            Publish();
        }
    }

    private void RecognitionProgressed(object? sender, RecognitionProgress progress)
    {
        var recognition = progress.Recognition;
        lock (_stateGate)
        {
            _confirmedPlates.ObserveFrame(
                recognition.SourceWidth,
                recognition.SourceHeight,
                progress.Diagnostics.Tracks);
            _liveOverlays = _settings.TrackingDiagnosticsEnabled
                ? DriveOverlayFactory.CreateDiagnosticOverlays(
                    progress.Diagnostics,
                    recognition.SourceWidth,
                    recognition.SourceHeight).ToArray()
                : DriveOverlayFactory.CreateReadingOverlays(
                    recognition.Observations,
                    recognition.SourceWidth,
                    recognition.SourceHeight).ToArray();
            _diagnostics = _diagnostics with { Recognition = progress.Diagnostics };
        }
        Publish();
    }

    private void PlateConfirmed(object? sender, RecognitionConfirmation result)
    {
        var sighting = result.Sighting;

        lock (_stateGate)
        {
            _sessionSightings[sighting.Id] = sighting;
            _uniqueVehicles.Clear();
            _uniqueVehicles.UnionWith(_sessionSightings.Values.Select(static item => item.NormalizedPlate));
            _recentSightings.RemoveAll(item => item.Id == sighting.Id);
            _recentSightings.Insert(0, sighting);
            if (_recentSightings.Count > 5) _recentSightings.RemoveRange(5, _recentSightings.Count - 5);
            _mostExpensive = _sessionSightings.Values
                .Where(static item => item.Vehicle?.CatalogPrice is not null)
                .OrderByDescending(static item => item.Vehicle!.CatalogPrice)
                .FirstOrDefault();
            _confirmedPlates.Confirm(result.Confirmation, sighting, result.Prior);
        }

        if (result.Confirmation.Revision == 0 && _settings.ConfirmationHaptic)
        {
            _deviceExperience.NotifyPlateConfirmed();
        }
        Publish();
    }

    private void RecognitionFailed(object? sender, Exception exception) => SetStatus($"Recognition frame skipped: {exception.Message} Scanning continues.", true);
    private void SourceFramesAvailable(object? sender, DriveFrameCountEventArgs args) => _performance.RecordSourceFrames(args.Count);
    private void PreviewFramesPresented(object? sender, DriveFrameCountEventArgs args) => _performance.RecordPreviewFrames(args.Count);
    private void PerformanceSampled(object? sender, DrivePerformanceSample sample)
    {
        lock (_stateGate)
        {
            if (!_driving)
            {
                return;
            }

            _diagnostics = _diagnostics with
            {
                Source = _diagnostics.Source with
                {
                    IntervalMilliseconds = sample.SourceFrameIntervalMilliseconds
                },
                Preview = _diagnostics.Preview with
                {
                    IntervalMilliseconds = _camera?.ReportsPreviewFrames == true
                        ? sample.PreviewFrameIntervalMilliseconds
                        : null
                }
            };
        }
        Publish();
    }
    private void CameraDiagnostic(object? sender, DriveInputDiagnostic diagnostic)
    {
        if (!_driving || diagnostic.IsError || diagnostic.Message.StartsWith("Camera active", StringComparison.Ordinal))
        {
            SetStatus(diagnostic.Message, diagnostic.IsError);
        }
    }
    private void CameraChoicesChanged(object? sender, IReadOnlyList<CameraChoice> choices)
    {
        lock (_stateGate) _cameraChoices = choices.ToArray();
        Publish();
    }
    private void SetStatus(string message) => SetStatus(message, false);
    private void SetStatus(string message, bool error)
    {
        lock (_stateGate)
        {
            _status = message;
            _hasError = error;
        }
        Publish();
    }

    private void Publish()
    {
        DriveSnapshot snapshot;
        lock (_stateGate) snapshot = CreateSnapshot();
        _dispatcher.Dispatch(() => SnapshotChanged?.Invoke(this, snapshot));
    }

    private DriveSnapshot CreateSnapshot() => new(
        _initializing,
        _ready,
        _driving,
        _stopping,
        _status,
        _hasError,
        _startedAt,
        CreateDiagnosticsSnapshot(),
        _uniqueVehicles.Count,
        _recentSightings.ToArray(),
        _mostExpensive,
        CreateOverlays(),
        _location.Latest is not null,
        _camera?.IsReady == true,
        _camera?.SupportsNetworkStreams == true,
        _cameraChoices.ToArray(),
        _camera?.SelectedCameraId ?? _settings.CameraId,
        _settings.TrackingDiagnosticsEnabled,
        _settings.RecognitionStatisticsEnabled,
        _settings.ShowRoadGuide);

    /// <summary>
    /// Confirmed plates are composed in at snapshot time rather than stored alongside the live
    /// overlays, so a new confirmation cannot drop the plates already on screen and a plate still
    /// expires while no further frames arrive. Caller must hold <c>_stateGate</c>.
    /// </summary>
    private IReadOnlyList<DriveOverlay> CreateOverlays() => _liveOverlays
        .Where(overlay => overlay.Kind != DriveOverlayKind.Reading
            || !_confirmedPlates.Suppresses(overlay.Bounds))
        .Concat(_confirmedPlates.CreateOverlays())
        .ToArray();

    private DriveDiagnosticsSnapshot CreateDiagnosticsSnapshot() => _diagnostics.WithSourceLabel(
        (_camera?.SelectedCameraId ?? _settings.CameraId) == DriveInputIds.NetworkLlHls
            ? "Decode interval"
            : "Capture interval");

    private static double DistanceMeters(GeoPoint from, GeoPoint to)
    {
        const double radius = 6_371_000;
        static double Radians(double degrees) => degrees * Math.PI / 180;
        var latitude = Radians(to.Latitude - from.Latitude);
        var longitude = Radians(to.Longitude - from.Longitude);
        var a = Math.Sin(latitude / 2) * Math.Sin(latitude / 2) + Math.Cos(Radians(from.Latitude)) * Math.Cos(Radians(to.Latitude)) * Math.Sin(longitude / 2) * Math.Sin(longitude / 2);
        return radius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // Waiting through StopDriveAsync also serializes disposal with an already-running stop.
        if (_driving || _stopping) await StopDriveAsync();
        _location.Stop();
        if (_recognition is not null)
        {
            _recognition.Progress -= RecognitionProgressed;
            _recognition.PlateConfirmed -= PlateConfirmed;
            _recognition.Failed -= RecognitionFailed;
            await _recognition.DisposeAsync();
        }
        _routeCancellation?.Dispose();
        _performance.Sampled -= PerformanceSampled;
        _performance.Dispose();
        _location.Dispose();
        _initializeGate.Dispose();
        _driveGate.Dispose();
    }
}
