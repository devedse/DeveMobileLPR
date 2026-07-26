using DeveMobileLPR.AndroidApp.Camera;
using DeveMobileLPR.AndroidApp.Infrastructure;
using DeveMobileLPR.AndroidApp.Recognition;
using DeveMobileLPR.AndroidApp.UI;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.AndroidApp.Services;

internal sealed record DriveOverlay(
    BoundingBox Bounds,
    int SourceWidth,
    int SourceHeight,
    string Title,
    string Detail,
    float Confidence,
    bool Confirmed);

internal sealed record DriveSnapshot(
    bool IsInitializing,
    bool IsReady,
    bool IsDriving,
    bool IsStopping,
    string Status,
    bool HasError,
    DateTimeOffset? StartedAt,
    int UniqueVehicles,
    IReadOnlyList<Sighting> RecentSightings,
    Sighting? MostExpensive,
    IReadOnlyList<DriveOverlay> Overlays,
    bool HasLocation,
    IReadOnlyList<CameraChoice> CameraChoices,
    string SelectedCameraId);

internal sealed class DriveCoordinator : IAsyncDisposable
{
    private readonly SqliteSightingRepository _repository;
    private readonly AppSettings _settings;
    private readonly RdwDatabaseService _rdw;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _driveGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly HashSet<string> _uniqueVehicles = new(StringComparer.Ordinal);
    private readonly List<Sighting> _recentSightings = [];
    private RecognitionSession? _recognition;
    private AndroidLocationTracker? _location;
    private CameraXFrameSource? _camera;
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
    private Sighting? _mostExpensive;
    private IReadOnlyList<DriveOverlay> _overlays = [];
    private IReadOnlyList<CameraChoice> _cameraChoices = [new("rear", "Rear cameras · automatic lens")];

    public DriveCoordinator(SqliteSightingRepository repository, AppSettings settings, RdwDatabaseService rdw)
    {
        _repository = repository;
        _settings = settings;
        _rdw = rdw;
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

            var detector = new OnnxYoloV9PlateDetector(models.Detector, diagnostic: SetStatus);
            var recognizer = new OnnxCctPlateRecognizer(models.Ocr, diagnostic: SetStatus);
            var pipeline = new PlateRecognitionPipeline(detector, recognizer);
            _location = new AndroidLocationTracker(context);
            _recognition = new RecognitionSession(
                pipeline,
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

    public void AttachCamera(CameraXFrameSource camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        _camera = camera;
        camera.Diagnostic += CameraDiagnostic;
        camera.CameraChoicesChanged += CameraChoicesChanged;
        _cameraChoices = camera.CameraChoices;
        camera.SelectCamera(_settings.CameraId);
        camera.SetZoom(_settings.Zoom);
        Publish();
    }

    public void DetachCamera(CameraXFrameSource camera)
    {
        camera.Diagnostic -= CameraDiagnostic;
        camera.CameraChoicesChanged -= CameraChoicesChanged;
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

            var cameraPermission = await Permissions.RequestAsync<Permissions.Camera>();
            if (cameraPermission != PermissionStatus.Granted)
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
                _uniqueVehicles.Clear();
                _recentSightings.Clear();
                _mostExpensive = null;
                _overlays = [];
                _confirmedOverlay = null;
                _hasError = false;
                _status = "Scanning · video stays on this device";
            }

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
                lock (_stateGate) _driving = false;
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
            }
            Publish();
            _camera?.Stop();
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

    public void SelectCamera(string cameraId)
    {
        _settings.CameraId = cameraId;
        _camera?.SelectCamera(_settings.CameraId);
        Publish();
    }

    public void RefreshSettings() => Publish();

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
        var recognition = progress.Recognition;
        var candidates = recognition.Observations.Select(observation => new DriveOverlay(
            observation.Detection.Bounds,
            recognition.SourceWidth,
            recognition.SourceHeight,
            FormatPlate(observation.Read.Text),
            $"Reading · {observation.Read.Confidence:P0}",
            observation.Detection.Confidence,
            false)).ToList();
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
                true);
            _confirmedOverlayUntil = DateTimeOffset.UtcNow.AddSeconds(3);
        }

        if (_settings.ConfirmationHaptic)
        {
            MainThread.BeginInvokeOnMainThread(PerformConfirmationHaptic);
        }
        Publish();
    }

    private void RecognitionFailed(object? sender, Exception exception) => SetStatus($"Recognition paused: {exception.Message}", true);
    private void CameraDiagnostic(object? sender, string message) { if (!_driving || message.StartsWith("Camera active", StringComparison.Ordinal)) SetStatus(message); }
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
        _uniqueVehicles.Count,
        _recentSightings.ToArray(),
        _mostExpensive,
        _overlays.ToArray(),
        _location?.Latest is not null,
        _cameraChoices.ToArray(),
        _camera?.SelectedCameraId ?? _settings.CameraId);

    private static void PerformConfirmationHaptic()
    {
        try
        {
            HapticFeedback.Default.Perform(HapticFeedbackType.Click);
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("RoadLens.Haptics", $"Confirmation haptic failed: {exception}");
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
        _initializeGate.Dispose();
        _driveGate.Dispose();
    }
}
