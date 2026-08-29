using Android.Content;
using Android.Views;
using AndroidX.Camera.View;
using AndroidX.Lifecycle;
using DeveMobileLPR.Application;
using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

/// <summary>
/// Selects between physical CameraX capture and Media3 LL-HLS while presenting
/// one stable input and telemetry contract to the drive coordinator.
/// </summary>
internal sealed class AndroidDriveVideoInput : IDriveVideoInput
{
    private readonly CameraXFrameSource _camera;
    private readonly UsbUvcFrameSource _uvc;
    private readonly AndroidHlsFrameSource _network;
    private readonly PreviewView _cameraPreview;
    private readonly UvcPreviewTextureView _uvcPreview;
    private readonly AndroidVideoTextureView _networkPreview;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private IReadOnlyList<CameraChoice> _cameraChoices;
    private string _selectedCameraId = DriveInputIds.RearCamera;
    private bool _running;
    private bool _disposed;
    private DriveZoomState _zoomState = DriveZoomState.Pending(1f);
    private float _requestedZoomRatio = 1f;

    public AndroidDriveVideoInput(
        Context context,
        ILifecycleOwner lifecycleOwner,
        PreviewView cameraPreview,
        UvcPreviewTextureView uvcPreview,
        AndroidVideoTextureView networkPreview,
        string networkStreamUrl,
        Func<int> recognitionFramesPerSecond,
        Func<bool> hasPendingRecognitionFrame,
        Func<Yuv420Frame, bool> submitFrame)
    {
        _cameraPreview = cameraPreview;
        _uvcPreview = uvcPreview;
        _networkPreview = networkPreview;
        _camera = new CameraXFrameSource(
            context,
            lifecycleOwner,
            cameraPreview,
            recognitionFramesPerSecond,
            frame => submitFrame(frame));
        _uvc = new UsbUvcFrameSource(
            context,
            uvcPreview,
            recognitionFramesPerSecond,
            hasPendingRecognitionFrame,
            submitFrame);
        _network = new AndroidHlsFrameSource(
            context,
            networkPreview,
            networkStreamUrl,
            recognitionFramesPerSecond,
            hasPendingRecognitionFrame,
            submitFrame);
        _cameraChoices = CombineChoices();

        _camera.Diagnostic += ChildDiagnostic;
        _camera.CameraChoicesChanged += ChildCameraChoicesChanged;
        _camera.SourceFramesAvailable += ChildSourceFramesAvailable;
        _camera.ZoomStateChanged += ChildCameraZoomStateChanged;
        _uvc.Diagnostic += ChildDiagnostic;
        _uvc.CameraChoicesChanged += ChildUvcCameraChoicesChanged;
        _uvc.SourceFramesAvailable += ChildSourceFramesAvailable;
        _uvc.ZoomStateChanged += ChildUvcZoomStateChanged;
        _network.Diagnostic += ChildDiagnostic;
        _network.SourceFramesAvailable += ChildSourceFramesAvailable;
        _network.PreviewFramesPresented += ChildPreviewFramesPresented;
        ApplyPreviewVisibility();
    }

    public event EventHandler<DriveInputDiagnostic>? Diagnostic;
    public event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged;
    public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
    public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented;
    public event EventHandler<DriveZoomState>? ZoomStateChanged;

    public IReadOnlyList<CameraChoice> CameraChoices => _cameraChoices;
    public string SelectedCameraId => _selectedCameraId;
    public bool ReportsPreviewFrames => _selectedCameraId == DriveInputIds.NetworkLlHls;
    public bool IsReady => _selectedCameraId switch
    {
        DriveInputIds.NetworkLlHls => _network.IsReady,
        var cameraId when DriveInputIds.IsUsbUvcCamera(cameraId) => _uvc.IsReady,
        _ => true
    };
    public bool SupportsNetworkStreams => true;
    public DriveZoomState ZoomState => _zoomState;

    public async Task InitializeAsync(string preferredCameraId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _camera.PrepareAsync(cancellationToken).ConfigureAwait(false);
            var exactChoice = _cameraChoices.FirstOrDefault(choice =>
                string.Equals(choice.Id, preferredCameraId, StringComparison.Ordinal));
            var migratedUvcChoice = preferredCameraId == DriveInputIds.ExternalCamera
                ? _cameraChoices.FirstOrDefault(choice => DriveInputIds.IsUsbUvcCamera(choice.Id))
                : null;
            var selectedId = exactChoice?.Id ?? migratedUvcChoice?.Id ?? _cameraChoices[0].Id;
            await SelectCameraCoreAsync(selectedId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_running)
            {
                return;
            }

            _running = true;
            await MainThread.InvokeOnMainThreadAsync(ApplyPreviewVisibility);
            try
            {
                await StartSelectedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _running = false;
                throw;
            }
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            await StopSelectedAsync().ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async Task SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_cameraChoices.Any(choice => string.Equals(choice.Id, cameraId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The selected Android video input is unavailable.", nameof(cameraId));
        }

        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SelectCameraCoreAsync(cameraId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    private async Task SelectCameraCoreAsync(string cameraId, CancellationToken cancellationToken)
    {
        if (string.Equals(_selectedCameraId, cameraId, StringComparison.Ordinal))
        {
            return;
        }

        var restart = _running;
        var previousCameraId = _selectedCameraId;
        if (restart)
        {
            await StopSelectedAsync().ConfigureAwait(false);
        }

        _selectedCameraId = cameraId;
        if (DriveInputIds.IsUsbUvcCamera(cameraId))
        {
            _uvc.SelectCamera(cameraId);
        }
        else if (cameraId != DriveInputIds.NetworkLlHls)
        {
            _camera.SelectCamera(cameraId);
        }
        await MainThread.InvokeOnMainThreadAsync(ApplyPreviewVisibility);
        ApplyRequestedZoom();
        PublishSelectedZoomState();

        if (!restart)
        {
            return;
        }

        try
        {
            await StartSelectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception switchException)
        {
            _selectedCameraId = previousCameraId;
            if (DriveInputIds.IsUsbUvcCamera(previousCameraId))
            {
                _uvc.SelectCamera(previousCameraId);
            }
            else if (previousCameraId != DriveInputIds.NetworkLlHls)
            {
                _camera.SelectCamera(previousCameraId);
            }
            await MainThread.InvokeOnMainThreadAsync(ApplyPreviewVisibility);
            ApplyRequestedZoom();
            PublishSelectedZoomState();
            try
            {
                await StartSelectedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                _running = false;
                throw new AggregateException(
                    "The selected input could not start and the previous input could not be resumed.",
                    switchException,
                    rollbackException);
            }
            throw;
        }
    }

    public void SetZoom(float zoomRatio)
    {
        _requestedZoomRatio = Math.Clamp(zoomRatio, 1f, 4f);
        ApplyRequestedZoom();
    }

    private void ApplyRequestedZoom()
    {
        if (DriveInputIds.IsUsbUvcCamera(_selectedCameraId))
        {
            _uvc.SetZoom(_requestedZoomRatio);
        }
        else if (_selectedCameraId != DriveInputIds.NetworkLlHls)
        {
            _camera.SetZoom(_requestedZoomRatio);
        }
        else
        {
            SetZoomState(DriveZoomState.Unavailable(_requestedZoomRatio));
        }
    }

    public void SetNetworkStreamUrl(string value)
    {
        _network.SetNetworkStreamUrl(value);
        if (_selectedCameraId == DriveInputIds.NetworkLlHls)
        {
            Diagnostic?.Invoke(this, new DriveInputDiagnostic(_network.IsReady
                ? "OME LL-HLS stream ready"
                : "Enter an HTTP or HTTPS .m3u8 URL for the OME LL-HLS stream."));
        }
    }

    private async Task StartSelectedAsync(CancellationToken cancellationToken)
    {
        if (_selectedCameraId == DriveInputIds.NetworkLlHls)
        {
            await _network.StartAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (DriveInputIds.IsUsbUvcCamera(_selectedCameraId))
        {
            await _uvc.StartAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
        {
            throw new UnauthorizedAccessException(
                "Camera access is required to recognize plates. You can enable it in Android settings.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        await _camera.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StopSelectedAsync()
    {
        if (_selectedCameraId == DriveInputIds.NetworkLlHls)
        {
            await _network.StopAsync().ConfigureAwait(false);
        }
        else if (DriveInputIds.IsUsbUvcCamera(_selectedCameraId))
        {
            await MainThread.InvokeOnMainThreadAsync(_uvc.Stop);
        }
        else
        {
            await MainThread.InvokeOnMainThreadAsync(_camera.Stop);
        }
    }

    private void ApplyPreviewVisibility()
    {
        var networkSelected = _selectedCameraId == DriveInputIds.NetworkLlHls;
        var uvcSelected = DriveInputIds.IsUsbUvcCamera(_selectedCameraId);
        _cameraPreview.Visibility = networkSelected || uvcSelected ? ViewStates.Gone : ViewStates.Visible;
        _uvcPreview.Visibility = uvcSelected ? ViewStates.Visible : ViewStates.Gone;
        _networkPreview.Visibility = networkSelected ? ViewStates.Visible : ViewStates.Gone;
    }

    private void ChildDiagnostic(object? sender, string message) => Diagnostic?.Invoke(
        this,
        new DriveInputDiagnostic(
            message,
            message.StartsWith("Could not", StringComparison.Ordinal)
                || message.Contains("failed", StringComparison.OrdinalIgnoreCase)));
    private void ChildSourceFramesAvailable(object? sender, DriveFrameCountEventArgs args) => SourceFramesAvailable?.Invoke(this, args);
    private void ChildPreviewFramesPresented(object? sender, DriveFrameCountEventArgs args) => PreviewFramesPresented?.Invoke(this, args);
    private void ChildCameraZoomStateChanged(object? sender, DriveZoomState state)
    {
        if (!DriveInputIds.IsUsbUvcCamera(_selectedCameraId)
            && _selectedCameraId != DriveInputIds.NetworkLlHls)
        {
            SetZoomState(state);
        }
    }
    private void ChildUvcZoomStateChanged(object? sender, DriveZoomState state)
    {
        if (DriveInputIds.IsUsbUvcCamera(_selectedCameraId)) SetZoomState(state);
    }

    private void PublishSelectedZoomState()
    {
        SetZoomState(_selectedCameraId switch
        {
            DriveInputIds.NetworkLlHls => DriveZoomState.Unavailable(_requestedZoomRatio),
            var cameraId when DriveInputIds.IsUsbUvcCamera(cameraId) => _uvc.ZoomState,
            _ => _camera.ZoomState
        });
    }

    private void SetZoomState(DriveZoomState state)
    {
        _zoomState = state;
        ZoomStateChanged?.Invoke(this, state);
    }

    private void ChildCameraChoicesChanged(object? sender, IReadOnlyList<CameraChoice> choices)
    {
        UpdateCombinedChoices();
    }

    private void ChildUvcCameraChoicesChanged(object? sender, IReadOnlyList<CameraChoice> choices) => UpdateCombinedChoices();

    private void UpdateCombinedChoices()
    {
        _cameraChoices = CombineChoices();
        var activeInputRemoved = _running
            && _selectedCameraId != DriveInputIds.NetworkLlHls
            && !_cameraChoices.Any(choice => choice.Id == _selectedCameraId);
        if (_selectedCameraId != DriveInputIds.NetworkLlHls
            && !_cameraChoices.Any(choice => choice.Id == _selectedCameraId))
        {
            _selectedCameraId = _cameraChoices[0].Id;
            ApplyRequestedZoom();
            PublishSelectedZoomState();
            MainThread.BeginInvokeOnMainThread(ApplyPreviewVisibility);
        }
        CameraChoicesChanged?.Invoke(this, _cameraChoices);
        if (activeInputRemoved)
        {
            _ = StartFallbackAfterRemovalAsync();
        }
    }

    private async Task StartFallbackAfterRemovalAsync()
    {
        await _switchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_running) return;
            await StartSelectedAsync(CancellationToken.None).ConfigureAwait(false);
            Diagnostic?.Invoke(this, new DriveInputDiagnostic("USB camera disconnected · switched to the available camera."));
        }
        catch (Exception exception)
        {
            _running = false;
            Diagnostic?.Invoke(this, new DriveInputDiagnostic(
                $"USB camera disconnected and fallback failed: {exception.Message}",
                IsError: true));
        }
        finally
        {
            _switchGate.Release();
        }
    }

    private IReadOnlyList<CameraChoice> CombineChoices() =>
    [
        .. _camera.CameraChoices.Where(choice => choice.Id != DriveInputIds.NetworkLlHls),
        .. _uvc.CameraChoices,
        new(DriveInputIds.NetworkLlHls, "OME LL-HLS stream")
    ];

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _switchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_running)
            {
                _running = false;
                await StopSelectedAsync().ConfigureAwait(false);
            }

            _camera.Diagnostic -= ChildDiagnostic;
            _camera.CameraChoicesChanged -= ChildCameraChoicesChanged;
            _camera.SourceFramesAvailable -= ChildSourceFramesAvailable;
            _camera.ZoomStateChanged -= ChildCameraZoomStateChanged;
            _uvc.Diagnostic -= ChildDiagnostic;
            _uvc.CameraChoicesChanged -= ChildUvcCameraChoicesChanged;
            _uvc.SourceFramesAvailable -= ChildSourceFramesAvailable;
            _uvc.ZoomStateChanged -= ChildUvcZoomStateChanged;
            _network.Diagnostic -= ChildDiagnostic;
            _network.SourceFramesAvailable -= ChildSourceFramesAvailable;
            _network.PreviewFramesPresented -= ChildPreviewFramesPresented;
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                _camera.Dispose();
                _uvc.Dispose();
                _network.Dispose();
            });
        }
        finally
        {
            _switchGate.Release();
            _switchGate.Dispose();
        }
    }
}
