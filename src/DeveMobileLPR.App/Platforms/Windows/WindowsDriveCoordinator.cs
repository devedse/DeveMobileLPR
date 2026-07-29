using DeveMobileLPR.App.Platforms.Windows;
using DeveMobileLPR.App.Recognition;
using DeveMobileLPR.App.Infrastructure;
using DeveMobileLPR.App.UI;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference.Models;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.App.Services;

internal sealed class DriveCoordinator : IAsyncDisposable
{
    private readonly SqliteSightingRepository _repository;
    private readonly AppSettings _settings;
    private readonly RdwDatabaseService _rdw;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _driveGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly DrivePerformanceMonitor _performance = new();
    private readonly HashSet<string> _uniqueVehicles = new(StringComparer.Ordinal);
    private readonly List<Sighting> _recentSightings = [];
    private RecognitionSession? _recognition;
    private WindowsWebcamFrameSource? _camera;
    private DriveOverlay? _confirmedOverlay;
    private DateTimeOffset _confirmedOverlayUntil;
    private long _activeTripId;
    private bool _initializing;
    private bool _ready;
    private bool _driving;
    private bool _stopping;
    private bool _disposed;
    private string _status = "Preparing the on-device recognition engine…";
    private bool _hasError;
    private DateTimeOffset? _startedAt;
    private double _videoFramesPerSecond;
    private double _aiFramesPerSecond;
    private Sighting? _mostExpensive;
    private IReadOnlyList<DriveOverlay> _overlays = [];
    private IReadOnlyList<CameraChoice> _cameraChoices = [];
    private string? _cameraDiagnostic;
    private bool _cameraHasError;
    public DriveCoordinator(SqliteSightingRepository repository, AppSettings settings, RdwDatabaseService rdw)
    {
        _repository = repository;
        _settings = settings;
        _rdw = rdw;
        _performance.Sampled += PerformanceSampled;
    }

    public event EventHandler<DriveSnapshot>? SnapshotChanged;
    public SqliteSightingRepository Repository => _repository;
    public DriveSnapshot Snapshot { get { lock (_stateGate) return CreateSnapshot(); } }
    public long? ActiveTripId { get { var value = Interlocked.Read(ref _activeTripId); return value == 0 ? null : value; } }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ready) return;
        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_ready) return;
            lock (_stateGate)
            {
                _initializing = true;
                _hasError = false;
                _status = "Opening your private trip library…";
            }
            Publish();
            await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            SetStatus("Verifying the bundled plate models…");
            var modelDirectory = Path.Combine(AppContext.BaseDirectory, "models");
            var detectorPath = Path.Combine(modelDirectory, ModelCatalog.Detector.FileName);
            var recognizerPath = Path.Combine(modelDirectory, ModelCatalog.Recognizer.FileName);
            await ModelArtifactVerifier.VerifyAsync(detectorPath, ModelCatalog.Detector, cancellationToken).ConfigureAwait(false);
            await ModelArtifactVerifier.VerifyAsync(recognizerPath, ModelCatalog.Recognizer, cancellationToken).ConfigureAwait(false);
            var pipeline = OnnxPlateRecognitionPipelineFactory.Create(detectorPath, recognizerPath, SetStatus);
            _recognition = new RecognitionSession(
                pipeline,
                _repository,
                new AppVehicleLookup(_rdw.DatabasePath),
                static () => null,
                () => ActiveTripId);
            _recognition.Progress += RecognitionProgressed;
            _recognition.PlateConfirmed += PlateConfirmed;
            _recognition.Failed += RecognitionFailed;
            lock (_stateGate)
            {
                _ready = true;
                _initializing = false;
                _status = _camera?.IsReady == true
                    ? "Ready · Windows webcam available"
                    : _cameraDiagnostic ?? "Ready · waiting for a Windows webcam";
                _hasError = _camera?.IsReady != true && _cameraHasError;
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

    public void AttachCamera(WindowsWebcamFrameSource camera)
    {
        _camera = camera;
        camera.Diagnostic += CameraDiagnostic;
        camera.CameraChoicesChanged += CameraChoicesChanged;
        camera.VideoFrameAvailable += VideoFrameAvailable;
        _cameraChoices = camera.CameraChoices;
        Publish();
    }

    public void DetachCamera(WindowsWebcamFrameSource camera)
    {
        camera.Diagnostic -= CameraDiagnostic;
        camera.CameraChoicesChanged -= CameraChoicesChanged;
        camera.VideoFrameAvailable -= VideoFrameAvailable;
        if (ReferenceEquals(_camera, camera)) _camera = null;
    }

    public async Task ResumeCameraAsync(WindowsWebcamFrameSource camera)
    {
        await _driveGate.WaitAsync();
        try
        {
            if (!_driving
                || _stopping
                || !ReferenceEquals(_camera, camera)
                || !camera.IsReady)
            {
                return;
            }

            await camera.StartAsync();
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_camera, camera))
            {
                SetStatus($"Could not resume the video input: {exception.Message}", true);
            }
        }
        finally
        {
            _driveGate.Release();
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
            if (_driving || _stopping) return;
            await InitializeAsync();
            if (!_ready) return;
            if (_camera?.IsReady != true)
            {
                SetStatus(_settings.CameraId == DriveInputIds.NetworkLlHls
                    ? "Enter a valid OME LL-HLS playlist URL before starting the drive."
                    : "No webcam is ready. Check Windows camera privacy settings and reconnect the camera.", true);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var trip = await _repository.StartTripAsync(now, null, CancellationToken.None);
            Interlocked.Exchange(ref _activeTripId, trip.Id);
            _recognition?.ResetTracking();
            lock (_stateGate)
            {
                _driving = true;
                _stopping = false;
                _startedAt = now;
                _videoFramesPerSecond = 0;
                _aiFramesPerSecond = 0;
                _uniqueVehicles.Clear();
                _recentSightings.Clear();
                _mostExpensive = null;
                _overlays = [];
                _confirmedOverlay = null;
                _hasError = false;
                _status = _settings.CameraId == DriveInputIds.NetworkLlHls
                    ? "Scanning OME LL-HLS stream · video is not saved"
                    : "Scanning webcam · video stays on this device";
            }
            _performance.Start();
            try
            {
                await _camera.StartAsync();
                MainThread.BeginInvokeOnMainThread(() => DeviceDisplay.Current.KeepScreenOn = true);
                Publish();
            }
            catch
            {
                _performance.Stop();
                lock (_stateGate)
                {
                    _driving = false;
                    _videoFramesPerSecond = 0;
                    _aiFramesPerSecond = 0;
                }
                await _repository.EndTripAsync(trip.Id, DateTimeOffset.UtcNow, null, CancellationToken.None);
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
            if (!_driving || _stopping) return;
            lock (_stateGate)
            {
                _driving = false;
                _stopping = true;
                _status = "Finishing your trip…";
                _overlays = [];
                _videoFramesPerSecond = 0;
                _aiFramesPerSecond = 0;
            }
            _performance.Stop();
            Publish();
            if (_camera is not null) await _camera.StopAsync();
            await Task.Delay(350);
            if (ActiveTripId is { } tripId)
            {
                await _repository.EndTripAsync(tripId, DateTimeOffset.UtcNow, null, CancellationToken.None);
            }
            Interlocked.Exchange(ref _activeTripId, 0);
            _recognition?.ResetTracking();
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

    public void SetZoom(float zoom) => _settings.Zoom = zoom;

    public void SetNetworkStreamUrl(string value)
    {
        _settings.NetworkStreamUrl = value;
        _camera?.SetNetworkStreamUrl(_settings.NetworkStreamUrl);
        Publish();
    }

    public void SelectCamera(string cameraId)
    {
        _settings.CameraId = cameraId;
        if (_camera is not null) _ = SelectCameraAsync(_camera, cameraId);
        Publish();
    }

    private async Task SelectCameraAsync(WindowsWebcamFrameSource camera, string cameraId)
    {
        try { await camera.SelectCameraAsync(cameraId); }
        catch (Exception exception) { SetStatus($"Could not switch video input: {exception.Message}", true); }
    }

    public void RefreshSettings() => Publish();

    public async Task DeleteHistoryAsync()
    {
        if (_driving || _stopping) throw new InvalidOperationException("Stop the active drive before deleting history.");
        await _repository.DeleteHistoryAsync(CancellationToken.None);
    }

    private void RecognitionProgressed(object? sender, RecognitionProgress progress)
    {
        _performance.RecordAiFrame();
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
            if (_confirmedOverlay is not null && DateTimeOffset.UtcNow < _confirmedOverlayUntil) candidates.Add(_confirmedOverlay);
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
            if (vehicle?.CatalogPrice is not null && (_mostExpensive?.Vehicle?.CatalogPrice is null || vehicle.CatalogPrice > _mostExpensive.Vehicle.CatalogPrice)) _mostExpensive = sighting;
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
        Publish();
    }

    private void RecognitionFailed(object? sender, Exception exception) => SetStatus($"Recognition paused: {exception.Message}", true);
    private void VideoFrameAvailable(object? sender, EventArgs args) => _performance.RecordVideoFrame();
    private void PerformanceSampled(object? sender, DrivePerformanceSample sample)
    {
        lock (_stateGate)
        {
            if (!_driving)
            {
                return;
            }

            _videoFramesPerSecond = sample.VideoFramesPerSecond;
            _aiFramesPerSecond = sample.AiFramesPerSecond;
        }
        Publish();
    }
    private void CameraDiagnostic(object? sender, string message)
    {
        var error = message.StartsWith("Could not", StringComparison.Ordinal)
            || message.Contains("failed", StringComparison.OrdinalIgnoreCase);
        lock (_stateGate)
        {
            _cameraDiagnostic = message;
            _cameraHasError = error;
        }
        SetStatus(message, error);
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
        _videoFramesPerSecond,
        _aiFramesPerSecond,
        _uniqueVehicles.Count,
        _recentSightings.ToArray(),
        _mostExpensive,
        _overlays.ToArray(),
        false,
        _camera?.IsReady == true,
        true,
        _cameraChoices.ToArray(),
        _camera?.SelectedCameraId ?? _settings.CameraId);
    private static string FormatPlate(string value)
    {
        var normalized = PlateText.Normalize(value);
        return normalized.Length == 6 ? PlateText.FormatDutchPlate(normalized) : value.ToUpperInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_driving) await StopDriveAsync();
        if (_recognition is not null)
        {
            _recognition.Progress -= RecognitionProgressed;
            _recognition.PlateConfirmed -= PlateConfirmed;
            _recognition.Failed -= RecognitionFailed;
            await _recognition.DisposeAsync();
        }
        _performance.Sampled -= PerformanceSampled;
        _performance.Dispose();
        _initializeGate.Dispose();
        _driveGate.Dispose();
    }
}
