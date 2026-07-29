using DeveMobileLPR.App.Camera;
using DeveMobileLPR.App.Infrastructure;
using DeveMobileLPR.App.Recognition;
using DeveMobileLPR.App.UI;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.App.Services;

internal sealed class DriveCoordinator : IAsyncDisposable
{
    private readonly SqliteSightingRepository _repository;
    private readonly AppSettings _settings;
    private readonly RdwDatabaseService _rdw;
    private readonly RecognitionTuningConfiguration _recognitionTuning;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _driveGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly DrivePerformanceMonitor _performance = new();
    private readonly HashSet<string> _uniqueVehicles = new(StringComparer.Ordinal);
    private readonly List<Sighting> _recentSightings = [];
    private RecognitionSession? _recognition;
    private AndroidLocationTracker? _location;
    private AndroidDriveFrameSource? _camera;
    private CancellationTokenSource? _routeCancellation;
    private Task? _routeWorker;
    private DriveOverlay? _confirmedOverlay;
    private DateTimeOffset _confirmedOverlayUntil;
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
    private double? _sourceFrameIntervalMilliseconds;
    private double? _previewFrameIntervalMilliseconds;
    private double? _recognitionFrameIntervalMilliseconds;
    private RecognitionStreamDiagnostics? _recognitionDiagnostics;
    private Sighting? _mostExpensive;
    private IReadOnlyList<DriveOverlay> _overlays = [];
    private IReadOnlyList<CameraChoice> _cameraChoices = [new("rear", "Rear cameras · automatic lens")];
    public DriveCoordinator(
        SqliteSightingRepository repository,
        AppSettings settings,
        RdwDatabaseService rdw,
        RecognitionTuningConfiguration recognitionTuning)
    {
        _repository = repository;
        _settings = settings;
        _rdw = rdw;
        _recognitionTuning = recognitionTuning;
        _performance.Sampled += PerformanceSampled;
    }

    public event EventHandler<DriveSnapshot>? SnapshotChanged;
    public SqliteSightingRepository Repository => _repository;
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
            var context = global::Android.App.Application.Context;
            var files = context.FilesDir?.AbsolutePath ?? FileSystem.AppDataDirectory;
            var models = await AndroidModelInstaller.EnsureInstalledAsync(
                context.Assets ?? throw new InvalidOperationException("Application assets are unavailable."),
                files,
                cancellationToken).ConfigureAwait(false);

            var pipeline = OnnxPlateRecognitionPipelineFactory.Create(
                models.Detector,
                models.Ocr,
                SetStatus,
                _recognitionTuning);
            _location = new AndroidLocationTracker(context);
            _recognition = new RecognitionSession(
                pipeline,
                _recognitionTuning,
                _repository,
                new AppVehicleLookup(_rdw.DatabasePath),
                () => _location.Latest,
                () => ActiveTripId);
            _recognition.Progress += RecognitionProgressed;
            _recognition.PlateConfirmed += PlateConfirmed;
            _recognition.Failed += RecognitionFailed;

            lock (_stateGate)
            {
                _ready = true;
                _initializing = false;
                _status = _rdw.IsInstalled
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

    public void AttachCamera(AndroidDriveFrameSource camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        _camera = camera;
        camera.Diagnostic += CameraDiagnostic;
        camera.CameraChoicesChanged += CameraChoicesChanged;
        camera.SourceFramesAvailable += SourceFramesAvailable;
        camera.PreviewFramesPresented += PreviewFramesPresented;
        _cameraChoices = camera.CameraChoices;
        _ = SelectCameraAsync(camera, _settings.CameraId);
        camera.SetZoom(_settings.Zoom);
        Publish();
    }

    public void DetachCamera(AndroidDriveFrameSource camera)
    {
        camera.Diagnostic -= CameraDiagnostic;
        camera.CameraChoicesChanged -= CameraChoicesChanged;
        camera.SourceFramesAvailable -= SourceFramesAvailable;
        camera.PreviewFramesPresented -= PreviewFramesPresented;
        if (ReferenceEquals(_camera, camera))
        {
            _camera = null;
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

            if (_camera.SelectedCameraId != DriveInputIds.NetworkLlHls
                && await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
            {
                SetStatus("Camera access is required to recognize plates. You can enable it in Android settings.", true);
                return;
            }

            if (_settings.TrackLocation)
            {
                var locationPermission = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
                if (locationPermission == PermissionStatus.Granted)
                {
                    _location?.Start();
                }
            }

            var now = DateTimeOffset.UtcNow;
            var trip = await _repository.StartTripAsync(now, _location?.Latest, CancellationToken.None);
            Interlocked.Exchange(ref _activeTripId, trip.Id);
            _recognition?.ResetTracking();
            lock (_stateGate)
            {
                _driving = true;
                _stopping = false;
                _startedAt = now;
                _sourceFrameIntervalMilliseconds = null;
                _previewFrameIntervalMilliseconds = null;
                _recognitionFrameIntervalMilliseconds = null;
                _recognitionDiagnostics = null;
                _uniqueVehicles.Clear();
                _recentSightings.Clear();
                _mostExpensive = null;
                _overlays = [];
                _confirmedOverlay = null;
                _hasError = false;
                _status = "Scanning · video stays on this device";
            }

            _performance.Start();
            try
            {
                await _camera.StartAsync();
                _camera.SetZoom(_settings.Zoom);
                if (_location?.Latest is { } initial)
                {
                    await _repository.AddTripPointAsync(trip.Id, now, initial, CancellationToken.None);
                    _lastRoutePoint = initial;
                    _lastRouteAt = now;
                }
                _routeCancellation = new CancellationTokenSource();
                _routeWorker = Task.Run(() => RecordRouteAsync(trip.Id, _routeCancellation.Token));
                MainThread.BeginInvokeOnMainThread(() => DeviceDisplay.Current.KeepScreenOn = true);
                Publish();
            }
            catch
            {
                _performance.Stop();
                lock (_stateGate)
                {
                    _driving = false;
                    _sourceFrameIntervalMilliseconds = null;
                    _previewFrameIntervalMilliseconds = null;
                    _recognitionFrameIntervalMilliseconds = null;
                    _recognitionDiagnostics = null;
                }
                await _repository.EndTripAsync(trip.Id, DateTimeOffset.UtcNow, _location?.Latest, CancellationToken.None);
                Interlocked.Exchange(ref _activeTripId, 0);
                throw;
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Could not start this drive: {exception.Message}", true);
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
                _overlays = [];
                _sourceFrameIntervalMilliseconds = null;
                _previewFrameIntervalMilliseconds = null;
                _recognitionFrameIntervalMilliseconds = null;
                _recognitionDiagnostics = null;
            }
            _performance.Stop();
            Publish();
            if (_camera is not null)
            {
                await _camera.StopAsync();
            }
            _routeCancellation?.Cancel();
            if (_routeWorker is not null)
            {
                try { await _routeWorker; } catch (OperationCanceledException) { }
            }

            // Give the one in-flight inference frame time to persist against the still-active trip.
            await Task.Delay(350);
            var tripId = ActiveTripId;
            if (tripId is not null)
            {
                await _repository.EndTripAsync(tripId.Value, DateTimeOffset.UtcNow, _location?.Latest, CancellationToken.None);
            }
            Interlocked.Exchange(ref _activeTripId, 0);
            _recognition?.ResetTracking();
            _location?.Stop();
            _routeCancellation?.Dispose();
            _routeCancellation = null;
            _routeWorker = null;
            MainThread.BeginInvokeOnMainThread(() => DeviceDisplay.Current.KeepScreenOn = false);

            lock (_stateGate)
            {
                _stopping = false;
                _startedAt = null;
                _status = "Trip saved · review it in History";
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

    private async Task SelectCameraAsync(AndroidDriveFrameSource camera, string cameraId)
    {
        try
        {
            if (_driving
                && cameraId != DriveInputIds.NetworkLlHls
                && await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
            {
                throw new UnauthorizedAccessException(
                    "Camera access is required to switch from the network stream to a phone camera.");
            }
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

    private void ResetInputPerformance(AndroidDriveFrameSource camera)
    {
        _performance.ResetSampleWindow();
        _recognition?.ResetTracking();
        lock (_stateGate)
        {
            _sourceFrameIntervalMilliseconds = null;
            _previewFrameIntervalMilliseconds = null;
            _recognitionFrameIntervalMilliseconds = null;
            _recognitionDiagnostics = null;
            _overlays = [];
            _confirmedOverlay = null;
        }
    }

    public void RefreshSettings()
    {
        if (!_settings.RecognitionDebugEnabled)
        {
            lock (_stateGate) _recognitionDiagnostics = null;
        }
        Publish();
    }

    public async Task DeleteHistoryAsync()
    {
        if (_driving || _stopping)
        {
            throw new InvalidOperationException("Stop the active drive before deleting history.");
        }
        await _repository.DeleteHistoryAsync(CancellationToken.None);
    }

    private async Task RecordRouteAsync(long tripId, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_location?.Latest is not { } point || point.AccuracyMeters is > 75)
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
        _performance.RecordRecognitionFrame();
        var recognition = progress.Recognition;
        List<DriveOverlay> candidates;
        if (_settings.RecognitionDebugEnabled)
        {
            candidates = progress.Diagnostics.Frame.Candidates.Select(candidate => new DriveOverlay(
                candidate.Detection.Bounds,
                recognition.SourceWidth,
                recognition.SourceHeight,
                string.IsNullOrWhiteSpace(candidate.ReadText) ? "Detector candidate" : FormatPlate(candidate.ReadText),
                candidate.OcrAttempted
                    ? $"det {candidate.Detection.Confidence:P0} · OCR {candidate.OcrConfidence:P0} · quality {candidate.Quality:P0}"
                    : $"det {candidate.Detection.Confidence:P0} · OCR not attempted",
                candidate.Detection.Confidence,
                DriveOverlayKind.Candidate)).ToList();
            candidates.AddRange(progress.Diagnostics.Associations
                .Where(static association => association.PredictedBounds is not null)
                .Select(association => new DriveOverlay(
                    association.PredictedBounds!.Value,
                    recognition.SourceWidth,
                    recognition.SourceHeight,
                    $"T{association.TrackId.ToString("N")[..6]} · prediction",
                    AssociationDiagnosticsFormatter.Format(association),
                    association.Score ?? 0,
                    DriveOverlayKind.Candidate)));
            candidates.AddRange(progress.Diagnostics.Tracks.Select(track =>
            {
                var association = progress.Diagnostics.Associations.FirstOrDefault(item => item.TrackId == track.TrackId);
                var associationText = association is null
                    ? "not observed this frame"
                    : AssociationDiagnosticsFormatter.Format(association);
                return new DriveOverlay(
                    track.Bounds,
                    recognition.SourceWidth,
                    recognition.SourceHeight,
                    $"T{track.TrackId.ToString("N")[..6]} · {FormatPlate(track.LastRead)}",
                    $"{track.ObservationCount} obs · {associationText}",
                    track.DetectorConfidence,
                    DriveOverlayKind.Track);
            }));
        }
        else
        {
            candidates = recognition.Observations.Select(observation => new DriveOverlay(
                observation.Detection.Bounds,
                recognition.SourceWidth,
                recognition.SourceHeight,
                FormatPlate(observation.Read.Text),
                $"Reading · {observation.Read.Confidence:P0}",
                observation.Detection.Confidence,
                DriveOverlayKind.Reading)).ToList();
        }
        lock (_stateGate)
        {
            if (_confirmedOverlay is not null
                && (_confirmedOverlay.SourceWidth != recognition.SourceWidth || _confirmedOverlay.SourceHeight != recognition.SourceHeight))
            {
                _confirmedOverlay = null;
            }
            if (_confirmedOverlay is not null && DateTimeOffset.UtcNow < _confirmedOverlayUntil)
            {
                candidates.Add(_confirmedOverlay);
            }
            _overlays = candidates;
            _recognitionDiagnostics = _settings.RecognitionDebugEnabled ? progress.Diagnostics : null;
        }
        Publish();
    }

    private void PlateConfirmed(object? sender, RecognitionConfirmation result)
    {
        var sighting = result.Sighting;
        var vehicle = sighting.Vehicle;
        var detail = vehicle is null
            ? "Confirmed · no RDW details"
            : string.Join(" · ", new[]
            {
                string.Join(' ', new[] { vehicle.Make, vehicle.Model }.Where(value => !string.IsNullOrWhiteSpace(value))),
                DisplayFormat.CompactPrice(vehicle.CatalogPrice),
                vehicle.BodyType
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        lock (_stateGate)
        {
            _uniqueVehicles.Add(sighting.NormalizedPlate);
            _recentSightings.RemoveAll(item => item.Id == sighting.Id);
            _recentSightings.Insert(0, sighting);
            if (_recentSightings.Count > 5) _recentSightings.RemoveRange(5, _recentSightings.Count - 5);
            if (vehicle?.CatalogPrice is not null && (_mostExpensive?.Vehicle?.CatalogPrice is null || vehicle.CatalogPrice > _mostExpensive.Vehicle.CatalogPrice))
            {
                _mostExpensive = sighting;
            }
            var source = _overlays.FirstOrDefault();
            _confirmedOverlay = new DriveOverlay(
                result.Confirmation.LastBounds,
                source?.SourceWidth ?? 1,
                source?.SourceHeight ?? 1,
                sighting.DisplayPlate,
                detail,
                sighting.Confidence,
                DriveOverlayKind.Confirmed);
            _confirmedOverlayUntil = DateTimeOffset.UtcNow.AddSeconds(3);
        }

        if (_settings.ConfirmationHaptic)
        {
            MainThread.BeginInvokeOnMainThread(PerformConfirmationHaptic);
        }
        Publish();
    }

    private void RecognitionFailed(object? sender, Exception exception) => SetStatus($"Recognition paused: {exception.Message}", true);
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

            _sourceFrameIntervalMilliseconds = sample.SourceFrameIntervalMilliseconds;
            _previewFrameIntervalMilliseconds = _camera?.ReportsPreviewFrames == true
                ? sample.PreviewFrameIntervalMilliseconds
                : null;
            _recognitionFrameIntervalMilliseconds = sample.RecognitionFrameIntervalMilliseconds;
        }
        Publish();
    }
    private void CameraDiagnostic(object? sender, string message)
    {
        var error = message.StartsWith("Could not", StringComparison.Ordinal)
            || message.Contains("failed", StringComparison.OrdinalIgnoreCase);
        if (!_driving || error || message.StartsWith("Camera active", StringComparison.Ordinal))
        {
            SetStatus(message, error);
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
        MainThread.BeginInvokeOnMainThread(() => SnapshotChanged?.Invoke(this, snapshot));
    }

    private DriveSnapshot CreateSnapshot() => new(
        _initializing,
        _ready,
        _driving,
        _stopping,
        _status,
        _hasError,
        _startedAt,
        _sourceFrameIntervalMilliseconds,
        _previewFrameIntervalMilliseconds,
        _recognitionFrameIntervalMilliseconds,
        _uniqueVehicles.Count,
        _recentSightings.ToArray(),
        _mostExpensive,
        _overlays.ToArray(),
        _location?.Latest is not null,
        _camera?.IsReady == true,
        true,
        _cameraChoices.ToArray(),
        _camera?.SelectedCameraId ?? _settings.CameraId,
        _recognitionDiagnostics,
        _settings.RecognitionDebugEnabled);

    private static void PerformConfirmationHaptic()
    {
        try
        {
            HapticFeedback.Default.Perform(HapticFeedbackType.Click);
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("DeveMobileLPR.Haptics", $"Confirmation haptic failed: {exception}");
        }
    }

    private static string FormatPlate(string value)
    {
        var normalized = PlateText.Normalize(value);
        return normalized.Length == 6 ? PlateText.FormatDutchPlate(normalized) : value.ToUpperInvariant();
    }

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
        if (_driving) await StopDriveAsync();
        _location?.Stop();
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
