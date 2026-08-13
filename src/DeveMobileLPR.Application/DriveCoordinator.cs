using DeveMobileLPR.Imaging;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application;

public sealed class DriveCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan RouteSampleInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinimumRouteInterval = TimeSpan.FromSeconds(30);
    private const float MaximumRouteAccuracyMeters = 75;
    private const double MinimumRouteDistanceMeters = 12;

    private readonly ISightingRepository _repository;
    private readonly IVehicleImageStore _vehicleImageStore;
    private readonly IDriveSettings _settings;
    private readonly IVehicleDataStatus _vehicleDataStatus;
    private readonly RecognitionTuningConfiguration _recognitionTuning;
    private readonly IRecognitionPipelineProvider _pipelineProvider;
    private readonly IVehicleLookup _vehicleLookup;
    private readonly IDriveLocationTrackerFactory _locationFactory;
    private readonly IDeviceExperience _deviceExperience;
    private readonly IApplicationDispatcher _dispatcher;
    private readonly IApplicationLog? _applicationLog;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _driveGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly DrivePerformanceMonitor _performance = new();
    private readonly Queue<string> _eventLog = new();
    private const int MaximumEventLogEntries = 12;
    /// <summary>The active drive, or null when idle. Guarded by <c>_stateGate</c>.</summary>
    private DriveTrip? _trip;
    private RecognitionSession? _recognition;
    private IDriveVideoInput? _camera;
    private Task? _cameraInitialization;
    private CancellationTokenSource? _routeCancellation;
    private Task? _routeWorker;
    private bool _initializing;
    private bool _ready;
    private bool _driving;
    private bool _stopping;
    private bool _disposed;
    private bool _cameraConfigurationReady;
    private string _status = "Preparing the on-device recognition engine…";
    private bool _hasError;
    private DriveDiagnosticsSnapshot _diagnostics = DriveDiagnosticsSnapshot.Empty;
    private readonly Dictionary<string, RecognitionStreamDiagnostics> _recognitionDiagnosticsBySource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _recognitionStageBySource = new(StringComparer.Ordinal);
    private IReadOnlyList<CameraChoice> _cameraChoices = [new("rear", "Rear cameras · automatic lens")];
    public DriveCoordinator(
        ISightingRepository repository,
        IVehicleImageStore vehicleImageStore,
        IDriveSettings settings,
        IVehicleDataStatus vehicleDataStatus,
        RecognitionTuningConfiguration recognitionTuning,
        IRecognitionPipelineProvider pipelineProvider,
        IVehicleLookup vehicleLookup,
        IDriveLocationTrackerFactory locationFactory,
        IDeviceExperience deviceExperience,
        IApplicationDispatcher dispatcher,
        IApplicationLog? applicationLog = null)
    {
        _repository = repository;
        _vehicleImageStore = vehicleImageStore;
        _settings = settings;
        _vehicleDataStatus = vehicleDataStatus;
        _recognitionTuning = recognitionTuning;
        _pipelineProvider = pipelineProvider;
        _vehicleLookup = vehicleLookup;
        _locationFactory = locationFactory;
        _deviceExperience = deviceExperience;
        _dispatcher = dispatcher;
        _applicationLog = applicationLog;
        _performance.Sampled += PerformanceSampled;
    }

    public event EventHandler<DriveSnapshot>? SnapshotChanged;
    public ISightingRepository Repository => _repository;
    public IVehicleImageStore VehicleImageStore => _vehicleImageStore;
    public DriveSnapshot Snapshot { get { lock (_stateGate) return CreateSnapshot(); } }
    public long? ActiveTripId { get { lock (_stateGate) return _trip?.TripId; } }
    public IReadOnlyList<DriveSourceCapability> SourceCapabilities => _camera?.SourceCapabilities ?? [];
    public DriveInputConfiguration InputConfiguration => _settings.InputConfiguration;

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
                CurrentLocation,
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
        _cameraConfigurationReady = false;
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
            _cameraConfigurationReady = false;
        }
    }

    public bool SubmitFrame(Yuv420Frame frame) => SubmitFrame("default", frame);

    public bool SubmitFrame(string sourceId, Yuv420Frame frame)
    {
        if (!_driving || _recognition is null)
        {
            frame.Dispose();
            return false;
        }

        return _recognition.Submit(sourceId, frame);
    }

    public bool HasPendingRecognitionFrameFor(string sourceId) => !_driving
        || _recognition is null
        || _recognition.HasPendingFrameFor(sourceId);

    public bool HasPendingRecognitionFrame => !_driving
        || _recognition is null
        || _recognition.HasPendingFrame;

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

            // A tracker per drive: it cannot report a position left over from the previous trip.
            var location = _locationFactory.Create();
            if (_settings.TrackLocation)
            {
                await location.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var now = DateTimeOffset.UtcNow;
            var startLocation = LocationAt(location, now);
            var row = await _repository.StartTripAsync(now, startLocation, CancellationToken.None);
            var trip = new DriveTrip(row.Id, now, location);
            _recognition?.ResetTracking();
            lock (_stateGate)
            {
                _trip = trip;
                _driving = true;
                _stopping = false;
                _diagnostics = DriveDiagnosticsSnapshot.Empty;
                _recognitionDiagnosticsBySource.Clear();
                _recognitionStageBySource.Clear();
                _hasError = false;
                _status = "Scanning · video stays on this device";
            }

            _performance.Start();
            startedCamera = camera;
            await camera.StartAsync();
            camera.SetZoom(_settings.Zoom);
            _routeCancellation = new CancellationTokenSource();
            _routeWorker = Task.Run(() => RecordRouteAsync(trip, startLocation, now, _routeCancellation.Token));
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
                _diagnostics = DriveDiagnosticsSnapshot.Empty;
                _recognitionDiagnosticsBySource.Clear();
                _recognitionStageBySource.Clear();
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
                _trip?.ClearOverlays();
                _diagnostics = DriveDiagnosticsSnapshot.Empty;
                _recognitionDiagnosticsBySource.Clear();
                _recognitionStageBySource.Clear();
            }
            _performance.Stop();
            Publish();

            var failures = await FinalizeDriveResourcesAsync(_camera, waitForInFlightRecognition: true);

            lock (_stateGate)
            {
                _stopping = false;
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

    public async Task ApplyInputConfigurationAsync(
        DriveInputConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (_driving || _stopping)
        {
            throw new InvalidOperationException("Stop the active drive before changing video sources.");
        }

        var camera = _camera ?? throw new InvalidOperationException("The camera preview is not available yet.");
        _cameraConfigurationReady = false;
        Publish();
        try
        {
            await camera.ApplyConfigurationAsync(configuration, cancellationToken).ConfigureAwait(false);
            _settings.InputConfiguration = configuration;
            _settings.CameraId = camera.SelectedCameraId;
            _cameraConfigurationReady = true;
            lock (_stateGate)
            {
                _hasError = false;
                _status = _vehicleDataStatus.IsAvailable
                    ? "Ready · video inputs configured · vehicle details available"
                    : "Ready · video inputs configured";
            }
            Publish();
        }
        catch (Exception exception)
        {
            _cameraConfigurationReady = false;
            SetStatus($"Could not apply video inputs: {exception.Message}", true);
            throw;
        }
    }
    public void SetZoom(float zoom)
    {
        _settings.Zoom = zoom;
        var configuration = _settings.InputConfiguration;
        if (configuration.Mode == DriveInputMode.Single)
        {
            var selectedId = configuration.SelectedSingleSourceId ?? "rear";
            _settings.InputConfiguration = configuration with
            {
                Sources = configuration.Sources
                    .Select(source => source.SourceId == selectedId ? source with { Zoom = zoom } : source)
                    .ToArray()
            };
        }
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
            _cameraConfigurationReady = false;
            Publish();
            await camera.SelectCameraAsync(cameraId);
            if (!ReferenceEquals(_camera, camera))
            {
                return;
            }
            _settings.CameraId = camera.SelectedCameraId;
            _cameraConfigurationReady = true;
            ResetInputPerformance();
            Publish();
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_camera, camera))
            {
                _cameraConfigurationReady = false;
                SetStatus($"Could not switch video input: {exception.Message}", true);
            }
        }
    }

    private void ResetInputPerformance()
    {
        _performance.ResetSampleWindow();
        _recognition?.ResetTracking();
        lock (_stateGate)
        {
            _diagnostics = DriveDiagnosticsSnapshot.Empty;
            _recognitionDiagnosticsBySource.Clear();
            _recognitionStageBySource.Clear();
            _trip?.ClearOverlays();
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
            _cameraConfigurationReady = false;
            await camera.InitializeAsync(preferredCameraId).ConfigureAwait(false);
            await camera.ApplyConfigurationAsync(_settings.InputConfiguration).ConfigureAwait(false);
            if (!ReferenceEquals(_camera, camera))
            {
                return;
            }

            _settings.CameraId = camera.SelectedCameraId;
            _cameraConfigurationReady = true;
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
                _cameraConfigurationReady = false;
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

        DriveTrip? trip;
        lock (_stateGate) trip = _trip;
        if (trip is not null)
        {
            var endedAt = DateTimeOffset.UtcNow;
            await CaptureFailureAsync(
                () => _repository.EndTripAsync(trip.TripId, endedAt, trip.LocationAt(endedAt), CancellationToken.None),
                failures);
        }

        // Clearing the field before disposing keeps a late reader from seeing a disposed tracker.
        lock (_stateGate) _trip = null;
        CaptureFailure(() => trip?.Dispose(), failures);
        CaptureFailure(() => _recognition?.ResetTracking(), failures);
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

    /// <summary>
    /// Samples the route for one trip. The last recorded point is a local rather than a field: only
    /// this loop needs it, and as a field it was both read without the state gate and left behind
    /// for the next trip to compare against.
    /// </summary>
    private async Task RecordRouteAsync(
        DriveTrip trip,
        GeoPoint? seedPoint,
        DateTimeOffset seedAt,
        CancellationToken cancellationToken)
    {
        var lastPoint = seedPoint;
        var lastAt = seedAt;
        if (seedPoint is { } start)
        {
            await _repository.AddTripPointAsync(trip.TripId, seedAt, start, cancellationToken).ConfigureAwait(false);
        }

        using var timer = new PeriodicTimer(RouteSampleInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow;
            if (trip.LocationAt(now) is not { } point || point.AccuracyMeters > MaximumRouteAccuracyMeters)
            {
                continue;
            }

            if (lastPoint is { } previous
                && DistanceMeters(previous, point) < MinimumRouteDistanceMeters
                && now - lastAt < MinimumRouteInterval)
            {
                continue;
            }

            await _repository.AddTripPointAsync(trip.TripId, now, point, cancellationToken).ConfigureAwait(false);
            lastPoint = point;
            lastAt = now;
            Publish();
        }
    }

    private void RecognitionProgressed(object? sender, RecognitionProgress progress)
    {
        var recognition = progress.Recognition;
        lock (_stateGate)
        {
            if (_trip is not { } trip)
            {
                return;
            }

            trip.ConfirmedPlates.ObserveFrame(
                recognition.SourceWidth,
                recognition.SourceHeight,
                progress.Diagnostics.Tracks,
                progress.SourceId);
            var liveOverlays = (_settings.TrackingDiagnosticsEnabled
                ? DriveOverlayFactory.CreateDiagnosticOverlays(
                    progress.Diagnostics,
                    recognition.SourceWidth,
                    recognition.SourceHeight).ToArray()
                : DriveOverlayFactory.CreateReadingOverlays(
                    recognition.Observations,
                    recognition.SourceWidth,
                    recognition.SourceHeight).ToArray())
                .Select(overlay => overlay with { SourceId = progress.SourceId })
                .ToArray();
            trip.SetLiveOverlays(progress.SourceId, liveOverlays);
            _diagnostics = _diagnostics with { Recognition = progress.Diagnostics };
            _recognitionDiagnosticsBySource[progress.SourceId] = progress.Diagnostics;
            var frame = progress.Diagnostics.Frame;
            var recognitionStage = frame.ObservationCount > 0 ? 3
                : frame.OcrAttemptCount > 0 ? 2
                : frame.DetectionCount > 0 ? 1
                : 0;
            var shouldReport = !_recognitionStageBySource.TryGetValue(progress.SourceId, out var previousStage)
                || recognitionStage > previousStage;
            _recognitionStageBySource[progress.SourceId] = Math.Max(recognitionStage, previousStage);
            if (shouldReport)
            {
                var sourceName = _camera?.SourceCapabilities.FirstOrDefault(source => source.Id == progress.SourceId)?.Name
                    ?? progress.SourceId;
                AppendEventLocked(
                    $"{sourceName}: AI analyzed frame rotated {recognition.RotationDegrees}° · " +
                    $"{frame.DetectionCount} detected · {frame.OcrAttemptCount} read · {frame.ObservationCount} accepted.");
            }
            _diagnostics = _diagnostics with
            {
                RecognitionSources = _recognitionDiagnosticsBySource
                    .Select(pair => new DriveSourceRecognitionDiagnostics(
                        pair.Key,
                        _camera?.SourceCapabilities.FirstOrDefault(source => source.Id == pair.Key)?.Name ?? pair.Key,
                        pair.Value))
                    .OrderBy(source => source.SourceName, StringComparer.Ordinal)
                    .ToArray()
            };
        }
        Publish();
    }

    private void PlateConfirmed(object? sender, RecognitionConfirmation result)
    {
        var sighting = result.Sighting;

        lock (_stateGate)
        {
            // A confirmation can arrive from the in-flight frame just after a drive stopped.
            if (_trip is not { } trip)
            {
                return;
            }

            trip.AddOrReplaceSighting(sighting);
            trip.ConfirmedPlates.Confirm(result.Confirmation, sighting, result.Prior, result.SourceId);
        }

        if (result.Confirmation.Revision == 0 && _settings.ConfirmationHaptic)
        {
            _deviceExperience.NotifyPlateConfirmed();
        }
        if (result.Confirmation.Revision == 0
            && result.Prior.SightingCount > 0
            && _settings.KnownVehicleSound != KnownVehicleSound.None)
        {
            _deviceExperience.NotifyKnownVehicle(_settings.KnownVehicleSound);
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
        AppendEvent(diagnostic.Message, diagnostic.IsError);
        if (!_driving || diagnostic.IsError || diagnostic.Message.StartsWith("Camera active", StringComparison.Ordinal))
        {
            SetStatus(diagnostic.Message, diagnostic.IsError, appendEvent: false);
        }
    }
    private void CameraChoicesChanged(object? sender, IReadOnlyList<CameraChoice> choices)
    {
        lock (_stateGate) _cameraChoices = choices.ToArray();
        Publish();
    }
    private void SetStatus(string message) => SetStatus(message, false);
    private void SetStatus(string message, bool error, bool appendEvent = true)
    {
        lock (_stateGate)
        {
            _status = message;
            _hasError = error;
            if (appendEvent)
            {
                AppendEventLocked(message, error);
            }
        }
        Publish();
    }

    private void AppendEvent(string message, bool isError = false)
    {
        lock (_stateGate)
        {
            AppendEventLocked(message, isError);
        }
        Publish();
    }

    private void AppendEventLocked(string message, bool isError = false)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss}  {message}";
        if (_eventLog.Count > 0 && _eventLog.Last().EndsWith(message, StringComparison.Ordinal))
        {
            return;
        }
        _applicationLog?.Write("Drive", message, isError);
        _eventLog.Enqueue(line);
        while (_eventLog.Count > MaximumEventLogEntries)
        {
            _eventLog.Dequeue();
        }
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
        _trip?.StartedAt,
        CreateDiagnosticsSnapshot(),
        _trip?.UniqueVehicleCount ?? 0,
        _trip?.RecentSightings() ?? [],
        _trip?.MostExpensive,
        CreateOverlays(),
        CurrentLocation() is not null,
        _camera?.IsReady == true && _cameraConfigurationReady,
        _camera?.SupportsNetworkStreams == true,
        _cameraChoices.ToArray(),
        _camera?.SelectedCameraId ?? _settings.CameraId,
        _settings.TrackingDiagnosticsEnabled,
        _settings.RecognitionStatisticsEnabled,
        _settings.ShowDriveEventLog,
        _settings.ShowRoadGuide,
        _eventLog.ToArray(),
        _settings.InputConfiguration.EnabledSources.Select(source => source.SourceId).ToArray());

    /// <summary>
    /// Confirmed plates are composed in at snapshot time rather than stored alongside the live
    /// overlays, so a new confirmation cannot drop the plates already on screen and a plate still
    /// expires while no further frames arrive. Caller must hold <c>_stateGate</c>.
    /// </summary>
    private IReadOnlyList<DriveOverlay> CreateOverlays()
    {
        if (_trip is not { } trip)
        {
            return [];
        }

        return trip.LiveOverlays
            .Where(overlay => overlay.Kind != DriveOverlayKind.Reading
                || !trip.ConfirmedPlates.Suppresses(overlay.Bounds, overlay.SourceId))
            .Concat(trip.ConfirmedPlates.CreateOverlays())
            .ToArray();
    }

    /// <summary>
    /// The position to stamp on data recorded right now, or null when there is no drive or no fix
    /// recent enough to trust. Safe to call from any thread.
    /// </summary>
    private GeoPoint? CurrentLocation()
    {
        DriveTrip? trip;
        lock (_stateGate) trip = _trip;
        return trip?.LocationAt(DateTimeOffset.UtcNow);
    }

    private static GeoPoint? LocationAt(IDriveLocationTracker tracker, DateTimeOffset now) =>
        tracker.Latest is { } fix && (now - fix.ObservedAt).Duration() <= DriveTrip.MaximumLocationAge
            ? fix.Point
            : null;

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
        _initializeGate.Dispose();
        _driveGate.Dispose();
    }
}
