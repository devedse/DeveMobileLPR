using AVFoundation;
using CoreFoundation;
using CoreMedia;
using CoreVideo;
using DeveMobileLPR.Application;
using DeveMobileLPR.Imaging;
using Foundation;

namespace DeveMobileLPR.App;

internal sealed class IosDriveFrameSource : IDriveVideoInput
{
    private readonly IosCameraPreviewView _preview;
    private readonly Func<int> _recognitionFramesPerSecond;
    private readonly Func<bool> _hasPendingFrame;
    private readonly Action<Yuv420Frame> _onFrame;
    private readonly FrameRateGate _gate = new(timestampFrequency: 1000);
    private readonly AVCaptureSession _session = new();
    private readonly AVCaptureVideoDataOutput _output = new();
    private readonly DispatchQueue _queue = new("nl.deve.mobilelpr.camera");
    private readonly SampleDelegate _delegate;
    private AVCaptureDeviceInput? _input;
    private AVCaptureDevice? _device;
    private long _sequence;
    private bool _initialized;
    private bool _running;
    private bool _disposed;
    private string _selectedCameraId = "rear";
    private float _requestedZoom = 1;
    private IReadOnlyList<CameraChoice> _choices = [new("rear", "Rear camera")];

    public IosDriveFrameSource(
        IosCameraPreviewView preview,
        Func<int> recognitionFramesPerSecond,
        Func<bool> hasPendingFrame,
        Action<Yuv420Frame> onFrame)
    {
        _preview = preview;
        _recognitionFramesPerSecond = recognitionFramesPerSecond;
        _hasPendingFrame = hasPendingFrame;
        _onFrame = onFrame;
        _delegate = new SampleDelegate(this);
    }

    public event EventHandler<DriveInputDiagnostic>? Diagnostic;
    public event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged;
    public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
    public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented { add { } remove { } }
    public IReadOnlyList<CameraChoice> CameraChoices => _choices;
    public string SelectedCameraId => _selectedCameraId;
    public bool IsReady => _initialized;
    public bool SupportsNetworkStreams => false;
    public bool ReportsPreviewFrames => false;

    public async Task InitializeAsync(string preferredCameraId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var permission = await Permissions.RequestAsync<Permissions.Camera>();
        cancellationToken.ThrowIfCancellationRequested();
        if (permission != PermissionStatus.Granted)
        {
            throw new UnauthorizedAccessException("Camera permission is required for Drive mode.");
        }

        _choices = AvailableChoices();
        _selectedCameraId = _choices.Any(choice => choice.Id == preferredCameraId)
            ? preferredCameraId
            : _choices[0].Id;
        ConfigureSession();
        _initialized = true;
        CameraChoicesChanged?.Invoke(this, _choices);
        Report("iPhone camera ready · AVFoundation NV12 capture");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("The camera has not been initialized.");
        _gate.Reset();
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_session.Running) _session.StartRunning();
        }, cancellationToken).ConfigureAwait(false);
        _running = true;
        Report("Camera active · recognition stays on this iPhone");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _running = false;
        _gate.Reset();
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_session.Running) _session.StopRunning();
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_choices.Any(choice => choice.Id == cameraId) || cameraId == _selectedCameraId) return Task.CompletedTask;
        var restart = _running;
        if (restart && _session.Running) _session.StopRunning();
        _selectedCameraId = cameraId;
        ConfigureSession();
        if (restart) _session.StartRunning();
        return Task.CompletedTask;
    }

    public void SetZoom(float zoomRatio)
    {
        _requestedZoom = Math.Max(1, zoomRatio);
        ApplyZoom();
    }

    public void SetNetworkStreamUrl(string value) { }

    private void ConfigureSession()
    {
        _session.BeginConfiguration();
        try
        {
            if (_input is not null) _session.RemoveInput(_input);
            if (_session.Outputs.Contains(_output)) _session.RemoveOutput(_output);
            _input?.Dispose();
            _device?.Dispose();

            var position = _selectedCameraId == "front"
                ? AVCaptureDevicePosition.Front
                : AVCaptureDevicePosition.Back;
            _device = AVCaptureDevice.GetDefaultDevice(
                AVCaptureDeviceType.BuiltInWideAngleCamera,
                AVMediaTypes.Video,
                position) ?? throw new InvalidOperationException("The selected iPhone camera is unavailable.");
            _input = AVCaptureDeviceInput.FromDevice(_device, out var error);
            if (_input is null) throw new InvalidOperationException(error?.LocalizedDescription ?? "The camera input could not be created.");
            if (!_session.CanAddInput(_input)) throw new InvalidOperationException("The camera input cannot be attached.");
            _session.AddInput(_input);

            _output.AlwaysDiscardsLateVideoFrames = true;
            _output.WeakVideoSettings = new CVPixelBufferAttributes
            {
                PixelFormatType = CVPixelFormatType.CV420YpCbCr8BiPlanarFullRange
            }.Dictionary;
            _output.SetSampleBufferDelegate(_delegate, _queue);
            if (!_session.CanAddOutput(_output)) throw new InvalidOperationException("The camera frame output cannot be attached.");
            _session.AddOutput(_output);
            _preview.Attach(_session);
            ApplyZoom();
        }
        finally
        {
            _session.CommitConfiguration();
        }
    }

    private static IReadOnlyList<CameraChoice> AvailableChoices()
    {
        var choices = new List<CameraChoice>();
        if (AVCaptureDevice.GetDefaultDevice(AVCaptureDeviceType.BuiltInWideAngleCamera, AVMediaTypes.Video, AVCaptureDevicePosition.Back) is { } rear)
        {
            choices.Add(new CameraChoice("rear", "Rear camera"));
            rear.Dispose();
        }
        if (AVCaptureDevice.GetDefaultDevice(AVCaptureDeviceType.BuiltInWideAngleCamera, AVMediaTypes.Video, AVCaptureDevicePosition.Front) is { } front)
        {
            choices.Add(new CameraChoice("front", "Front camera"));
            front.Dispose();
        }
        if (choices.Count == 0) throw new InvalidOperationException("No iPhone camera is available.");
        return choices;
    }

    private void ApplyZoom()
    {
        if (_device is null) return;
        if (!_device.LockForConfiguration(out var error))
        {
            Report(error?.LocalizedDescription ?? "Camera zoom could not be configured.", true);
            return;
        }
        try
        {
            var maximum = (float)Math.Min(_device.ActiveFormat.VideoMaxZoomFactor, 4);
            _device.VideoZoomFactor = Math.Clamp(_requestedZoom, 1, maximum);
        }
        finally
        {
            _device.UnlockForConfiguration();
        }
    }

    private unsafe void Receive(CMSampleBuffer sampleBuffer)
    {
        if (!_running) return;
        SourceFramesAvailable?.Invoke(this, new DriveFrameCountEventArgs(1));
        if (_hasPendingFrame() || !_gate.TryAcquire(Environment.TickCount64, _recognitionFramesPerSecond())) return;
        using var pixelBuffer = sampleBuffer.GetImageBuffer() as CVPixelBuffer;
        if (pixelBuffer is null || pixelBuffer.PlaneCount != 2) return;
        pixelBuffer.Lock(CVPixelBufferLock.ReadOnly);
        try
        {
            var width = checked((int)pixelBuffer.Width);
            var height = checked((int)pixelBuffer.Height);
            var yStride = checked((int)pixelBuffer.GetBytesPerRowOfPlane(0));
            var uvStride = checked((int)pixelBuffer.GetBytesPerRowOfPlane(1));
            var y = new ReadOnlySpan<byte>((void*)pixelBuffer.GetBaseAddressOfPlane(0), checked(yStride * height));
            var uvHeight = (height + 1) / 2;
            var uv = new ReadOnlySpan<byte>((void*)pixelBuffer.GetBaseAddressOfPlane(1), checked(uvStride * uvHeight));
            _onFrame(BiPlanarNv12FrameFactory.Create(
                y, yStride, uv, uvStride, width, height,
                Interlocked.Increment(ref _sequence), DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            Report($"iPhone frame ingestion failed: {exception.Message}", true);
        }
        finally
        {
            pixelBuffer.Unlock(CVPixelBufferLock.ReadOnly);
        }
    }

    private void Report(string message, bool error = false) =>
        Diagnostic?.Invoke(this, new DriveInputDiagnostic(message, error));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _output.SetSampleBufferDelegate(null, null);
        _input?.Dispose();
        _device?.Dispose();
        _delegate.Dispose();
        _output.Dispose();
        _session.Dispose();
        _queue.Dispose();
    }

    private sealed class SampleDelegate(IosDriveFrameSource owner) : AVCaptureVideoDataOutputSampleBufferDelegate
    {
        public override void DidOutputSampleBuffer(
            AVCaptureOutput captureOutput,
            CMSampleBuffer sampleBuffer,
            AVCaptureConnection connection) => owner.Receive(sampleBuffer);
    }
}
