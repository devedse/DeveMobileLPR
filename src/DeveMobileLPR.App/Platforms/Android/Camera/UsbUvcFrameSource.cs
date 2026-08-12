using Android.Content;
using Android.Hardware.Usb;
using Com.Deve.Mobilelpr.Uvc;
using DeveMobileLPR.Application;
using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

/// <summary>Discovers and streams UVC devices directly through Android USB host + libusb/libuvc.</summary>
internal sealed class UsbUvcFrameSource : Java.Lang.Object, IDriveFrameSourceTelemetry, IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private readonly UsbManager _usbManager;
    private readonly UvcPreviewTextureView _preview;
    private readonly Func<int> _recognitionFramesPerSecond;
    private readonly Func<bool> _hasPendingRecognitionFrame;
    private readonly Func<Yuv420Frame, bool> _submitFrame;
    private readonly FrameRateGate _recognitionFrameGate = new(timestampFrequency: 1000);
    private readonly UvcBridgeListener _listener;
    private readonly UvcCameraBridge _bridge;
    private IReadOnlyList<CameraChoice> _cameraChoices = [];
    private TaskCompletionSource? _opened;
    private string? _selectedCameraId;
    private long _sequence;
    private volatile bool _running;
    private bool _disposed;

    public UsbUvcFrameSource(
        Context context,
        UvcPreviewTextureView preview,
        Func<int> recognitionFramesPerSecond,
        Func<bool> hasPendingRecognitionFrame,
        Func<Yuv420Frame, bool> submitFrame)
    {
        _usbManager = (UsbManager?)context.GetSystemService(Context.UsbService)
            ?? throw new InvalidOperationException("Android USB host service is unavailable.");
        _preview = preview;
        _recognitionFramesPerSecond = recognitionFramesPerSecond;
        _hasPendingRecognitionFrame = hasPendingRecognitionFrame;
        _submitFrame = submitFrame;
        _listener = new UvcBridgeListener(this);
        _bridge = new UvcCameraBridge(context, _listener);
        _preview.SurfaceChanged += PreviewSurfaceChanged;
        RefreshCameraChoices();
    }

    public event EventHandler<string>? Diagnostic;
    public event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged;
    public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
    public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented
    {
        add { }
        remove { }
    }

    public IReadOnlyList<CameraChoice> CameraChoices => _cameraChoices;
    public bool ReportsPreviewFrames => false;
    public bool IsReady => _cameraChoices.Count > 0;

    public void SelectCamera(string cameraId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_cameraChoices.Any(choice => choice.Id == cameraId))
        {
            throw new ArgumentException("The selected USB/UVC camera is no longer connected.", nameof(cameraId));
        }
        _selectedCameraId = cameraId;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cameraId = _selectedCameraId
            ?? throw new InvalidOperationException("Select a USB/UVC camera before starting it.");
        var device = FindDevice(cameraId)
            ?? throw new InvalidOperationException("The selected USB/UVC camera is no longer connected.");

        _recognitionFrameGate.Reset();
        _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running = true;
        await MainThread.InvokeOnMainThreadAsync(() =>
            _bridge.SelectDevice(device, _preview.PreviewSurface));

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartupTimeout);
            await _opened.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            Stop();
            throw new TimeoutException(
                $"The USB camera did not begin streaming within {StartupTimeout.TotalSeconds:0} seconds.",
                exception);
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        _running = false;
        _opened?.TrySetCanceled();
        _opened = null;
        _bridge.CloseCamera();
    }

    private void PreviewSurfaceChanged(object? sender, global::Android.Views.Surface? surface)
    {
        if (!_disposed)
        {
            _bridge.SetPreviewSurface(surface);
        }
    }

    private void RefreshCameraChoices()
    {
        var devices = _usbManager.DeviceList?.Values
            .Where(UvcCameraBridge.IsUvcDevice)
            .OrderBy(device => device.VendorId)
            .ThenBy(device => device.ProductId)
            .ThenBy(device => device.DeviceName, StringComparer.Ordinal)
            .ToArray() ?? [];
        _cameraChoices = devices.Select(device => new CameraChoice(GetCameraId(device), GetDisplayName(device))).ToArray();
        CameraChoicesChanged?.Invoke(this, _cameraChoices);
    }

    private UsbDevice? FindDevice(string cameraId) =>
        _usbManager.DeviceList?.Values.FirstOrDefault(device =>
            UvcCameraBridge.IsUvcDevice(device) && GetCameraId(device) == cameraId);

    private static string GetCameraId(UsbDevice device) =>
        DriveInputIds.UsbUvcCameraPrefix + Uri.EscapeDataString(device.DeviceName ?? device.DeviceId.ToString());

    private static string GetDisplayName(UsbDevice device)
    {
        string? product = null;
        try
        {
            product = device.ProductName;
        }
        catch (Java.Lang.SecurityException)
        {
            // Android may withhold USB descriptors until the user grants device permission.
        }
        return string.IsNullOrWhiteSpace(product)
            ? $"USB/UVC camera · {device.VendorId:X4}:{device.ProductId:X4}"
            : $"{product} · USB/UVC";
    }

    private void OnAttached(UsbDevice? device)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            RefreshCameraChoices();
            Diagnostic?.Invoke(this, "USB/UVC camera connected.");
        });
    }

    private void OnDetached(UsbDevice? device)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var detachedSelected = device is not null && _selectedCameraId == GetCameraId(device);
            if (detachedSelected)
            {
                _running = false;
                _opened?.TrySetException(new IOException("The USB/UVC camera was disconnected."));
            }
            RefreshCameraChoices();
            Diagnostic?.Invoke(this, detachedSelected
                ? "USB/UVC camera disconnected while active."
                : "USB/UVC camera disconnected.");
        });
    }

    private void OnOpened(UsbDevice? device, int width, int height, int framesPerSecond)
    {
        Diagnostic?.Invoke(this, $"USB/UVC stream active · libusb/libuvc · {width}x{height}@{framesPerSecond}");
        _opened?.TrySetResult();
    }

    private void OnPermissionDenied(UsbDevice? device)
    {
        _running = false;
        _opened?.TrySetException(new UnauthorizedAccessException(
            "USB camera access was denied. Reconnect the camera and allow DeveMobileLPR to use it."));
    }

    private void OnError(UsbDevice? device, string? message)
    {
        var exception = new IOException(string.IsNullOrWhiteSpace(message)
            ? "The USB/UVC camera failed."
            : $"The USB/UVC camera failed: {message}");
        _running = false;
        _opened?.TrySetException(exception);
        Diagnostic?.Invoke(this, exception.Message);
    }

    private void OnFrame(Java.Nio.ByteBuffer? buffer, int width, int height)
    {
        if (!_running || buffer is null)
        {
            return;
        }

        SourceFramesAvailable?.Invoke(this, new DriveFrameCountEventArgs(1));
        if (_hasPendingRecognitionFrame()
            || !_recognitionFrameGate.TryAcquire(Environment.TickCount64, _recognitionFramesPerSecond()))
        {
            return;
        }

        try
        {
            var requiredLength = checked(width * (height + (height + 1) / 2));
            if (buffer.Capacity() < requiredLength)
            {
                Diagnostic?.Invoke(this, $"USB/UVC frame was shorter than NV21 {width}x{height} requires.");
                return;
            }

            var packed = GC.AllocateUninitializedArray<byte>(requiredLength);
            buffer.Rewind();
            buffer.Get(packed, 0, requiredLength);
            var frame = Nv21FrameFactory.Create(
                packed,
                width,
                width,
                height,
                Interlocked.Increment(ref _sequence),
                DateTimeOffset.UtcNow,
                rotationDegrees: 0);
            _submitFrame(frame);
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke(this, $"Could not process a USB/UVC frame: {exception.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _running = false;
            _preview.SurfaceChanged -= PreviewSurfaceChanged;
            _bridge.Release();
            _bridge.Dispose();
            _listener.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class UvcBridgeListener(UsbUvcFrameSource owner) : Java.Lang.Object, UvcCameraBridge.IListener
    {
        public void OnAttached(UsbDevice? device) => owner.OnAttached(device);
        public void OnDetached(UsbDevice? device) => owner.OnDetached(device);
        public void OnPermissionDenied(UsbDevice? device) => owner.OnPermissionDenied(device);
        public void OnOpened(UsbDevice? device, int width, int height, int framesPerSecond) =>
            owner.OnOpened(device, width, height, framesPerSecond);
        public void OnFrame(Java.Nio.ByteBuffer? frame, int width, int height) => owner.OnFrame(frame, width, height);
        public void OnError(UsbDevice? device, string? message) => owner.OnError(device, message);
    }
}
