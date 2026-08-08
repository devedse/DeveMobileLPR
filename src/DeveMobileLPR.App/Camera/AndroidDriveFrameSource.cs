using Android.Content;
using Android.Views;
using AndroidX.Camera.View;
using AndroidX.Lifecycle;
using DeveMobileLPR.Application;
using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.App.Camera;

/// <summary>
/// Selects between physical CameraX capture and Media3 LL-HLS while presenting
/// one stable input and telemetry contract to the drive coordinator.
/// </summary>
internal sealed class AndroidDriveFrameSource : IDriveVideoInput
{
    private readonly CameraXFrameSource _camera;
    private readonly AndroidHlsFrameSource _network;
    private readonly PreviewView _cameraPreview;
    private readonly AndroidVideoTextureView _networkPreview;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private IReadOnlyList<CameraChoice> _cameraChoices;
    private string _selectedCameraId = "rear";
    private bool _running;
    private bool _disposed;

    public AndroidDriveFrameSource(
        Context context,
        ILifecycleOwner lifecycleOwner,
        PreviewView cameraPreview,
        AndroidVideoTextureView networkPreview,
        string networkStreamUrl,
        Func<int> recognitionFramesPerSecond,
        Func<bool> hasPendingRecognitionFrame,
        Func<Yuv420Frame, bool> submitFrame)
    {
        _cameraPreview = cameraPreview;
        _networkPreview = networkPreview;
        _camera = new CameraXFrameSource(
            context,
            lifecycleOwner,
            cameraPreview,
            recognitionFramesPerSecond,
            frame => submitFrame(frame));
        _network = new AndroidHlsFrameSource(
            context,
            networkPreview,
            networkStreamUrl,
            recognitionFramesPerSecond,
            hasPendingRecognitionFrame,
            submitFrame);
        _cameraChoices = WithNetworkChoice(_camera.CameraChoices);

        _camera.Diagnostic += ChildDiagnostic;
        _camera.CameraChoicesChanged += ChildCameraChoicesChanged;
        _camera.SourceFramesAvailable += ChildSourceFramesAvailable;
        _network.Diagnostic += ChildDiagnostic;
        _network.SourceFramesAvailable += ChildSourceFramesAvailable;
        _network.PreviewFramesPresented += ChildPreviewFramesPresented;
        ApplyPreviewVisibility();
    }

    public event EventHandler<DriveInputDiagnostic>? Diagnostic;
    public event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged;
    public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
    public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented;

    public IReadOnlyList<CameraChoice> CameraChoices => _cameraChoices;
    public string SelectedCameraId => _selectedCameraId;
    public bool ReportsPreviewFrames => _selectedCameraId == DriveInputIds.NetworkLlHls;
    public bool IsReady => _selectedCameraId == DriveInputIds.NetworkLlHls ? _network.IsReady : true;
    public bool SupportsNetworkStreams => true;

    public Task InitializeAsync(string preferredCameraId, CancellationToken cancellationToken = default) =>
        SelectCameraAsync(preferredCameraId, cancellationToken);

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
        if (cameraId is not ("rear" or "front")
            && !_cameraChoices.Any(choice => string.Equals(choice.Id, cameraId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The selected Android video input is unavailable.", nameof(cameraId));
        }

        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
            if (cameraId != DriveInputIds.NetworkLlHls)
            {
                _camera.SelectCamera(cameraId);
            }
            await MainThread.InvokeOnMainThreadAsync(ApplyPreviewVisibility);

            if (restart)
            {
                try
                {
                    await StartSelectedAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception switchException)
                {
                    _selectedCameraId = previousCameraId;
                    if (previousCameraId != DriveInputIds.NetworkLlHls)
                    {
                        _camera.SelectCamera(previousCameraId);
                    }
                    await MainThread.InvokeOnMainThreadAsync(ApplyPreviewVisibility);
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
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public void SetZoom(float zoomRatio) => _camera.SetZoom(zoomRatio);

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

        cancellationToken.ThrowIfCancellationRequested();
        if (await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
        {
            throw new UnauthorizedAccessException(
                "Camera access is required to recognize plates. You can enable it in Android settings.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        await _camera.StartAsync().ConfigureAwait(false);
    }

    private async Task StopSelectedAsync()
    {
        if (_selectedCameraId == DriveInputIds.NetworkLlHls)
        {
            await _network.StopAsync().ConfigureAwait(false);
        }
        else
        {
            await MainThread.InvokeOnMainThreadAsync(_camera.Stop);
        }
    }

    private void ApplyPreviewVisibility()
    {
        var networkSelected = _selectedCameraId == DriveInputIds.NetworkLlHls;
        _cameraPreview.Visibility = networkSelected ? ViewStates.Gone : ViewStates.Visible;
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

    private void ChildCameraChoicesChanged(object? sender, IReadOnlyList<CameraChoice> choices)
    {
        _cameraChoices = WithNetworkChoice(choices);
        if (_selectedCameraId != DriveInputIds.NetworkLlHls
            && !_cameraChoices.Any(choice => choice.Id == _selectedCameraId))
        {
            _selectedCameraId = _cameraChoices[0].Id;
        }
        CameraChoicesChanged?.Invoke(this, _cameraChoices);
    }

    private static IReadOnlyList<CameraChoice> WithNetworkChoice(IReadOnlyList<CameraChoice> cameras) =>
        [.. cameras.Where(choice => choice.Id != DriveInputIds.NetworkLlHls), new(DriveInputIds.NetworkLlHls, "OME LL-HLS stream")];

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
            _network.Diagnostic -= ChildDiagnostic;
            _network.SourceFramesAvailable -= ChildSourceFramesAvailable;
            _network.PreviewFramesPresented -= ChildPreviewFramesPresented;
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                _camera.Dispose();
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
