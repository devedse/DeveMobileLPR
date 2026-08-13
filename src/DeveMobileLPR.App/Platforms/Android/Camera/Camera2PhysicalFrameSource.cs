using System.Buffers;
using System.Runtime.Versioning;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Views;
using DeveMobileLPR.Application;
using DeveMobileLPR.Imaging;
using Java.Lang;
using Java.Util.Concurrent;
using AndroidSize = Android.Util.Size;
using Exception = System.Exception;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class Camera2PhysicalFrameSource : IDisposable
{
    private readonly Context _context;
    private readonly Func<int> _recognitionFramesPerSecond;
    private readonly Func<string, Yuv420Frame, bool> _submitFrame;
    private readonly List<ConfiguredSource> _sources = [];
    private readonly List<Surface> _previewSurfaces = [];
    private readonly List<ImageReader> _readers = [];
    private readonly List<ImageAvailableListener> _listeners = [];
    private CameraDevice? _device;
    private CameraCaptureSession? _session;
    private CameraDevice.StateCallback? _deviceCallback;
    private CameraCaptureSession.StateCallback? _sessionCallback;
    private HandlerThread? _imageThread;
    private Handler? _imageHandler;
    private bool _running;
    private bool _disposed;

    public Camera2PhysicalFrameSource(
        Context context,
        Func<int> recognitionFramesPerSecond,
        Func<string, Yuv420Frame, bool> submitFrame)
    {
        _context = context;
        _recognitionFramesPerSecond = recognitionFramesPerSecond;
        _submitFrame = submitFrame;
    }

    public event EventHandler<string>? Diagnostic;
    public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
    public event Action<string, string, bool>? SourceStatusChanged;

    public void Configure(
        IReadOnlyList<(DriveSourceCapability Capability, DriveSourceProfile Profile, TextureView Preview)> sources)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            throw new InvalidOperationException("Stop Camera2 capture before changing its configuration.");
        }
        if (sources.Count != 2
            || sources.Any(source => source.Capability.Kind != DriveSourceKind.PhysicalCamera)
            || sources.Select(source => source.Capability.LogicalCameraId).Distinct().Count() != 1)
        {
            throw new NotSupportedException(
                "The Camera2 physical path requires two physical lenses behind one logical camera.");
        }

        _sources.Clear();
        _sources.AddRange(sources.Select(source => new ConfiguredSource(
            source.Capability,
            source.Profile,
            source.Preview,
            _recognitionFramesPerSecond)));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            return;
        }

        await WaitForTexturesAsync(cancellationToken).ConfigureAwait(false);
        var manager = _context.GetSystemService(Context.CameraService) as CameraManager
            ?? throw new InvalidOperationException("Android returned no CameraManager.");

        PrepareOutputs();
        if (!OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            throw new PlatformNotSupportedException("Physical camera outputs require Android 9 or newer.");
        }
        try
        {
            Diagnostic?.Invoke(this,
                "Starting Camera2 physical streams: " + string.Join(" + ", _sources.Select(source =>
                    $"{source.Capability.Name} at {source.Profile.Resolution}, {source.Profile.Zoom:0.0}x")));
            foreach (var source in _sources)
            {
                SourceStatusChanged?.Invoke(source.Capability.Id, "WAITING FOR CAMERA FRAMES", false);
            }
            var logicalId = _sources[0].Capability.LogicalCameraId
                ?? throw new InvalidOperationException("Logical camera ID is missing.");
            _device = await OpenCameraAsync(manager, logicalId, cancellationToken).ConfigureAwait(false);
            _session = await CreateSessionAsync(_device, cancellationToken).ConfigureAwait(false);
            var requestBuilder = _device.CreateCaptureRequest(CameraTemplate.Preview)
                ?? throw new InvalidOperationException("Camera2 could not create a repeating request.");
            foreach (var surface in _previewSurfaces)
            {
                requestBuilder.AddTarget(surface);
            }
            foreach (var reader in _readers)
            {
                requestBuilder.AddTarget(reader.Surface!);
            }
            foreach (var source in _sources)
            {
                ApplyPhysicalZoom(requestBuilder, manager, source);
            }

            var request = requestBuilder.Build()
                ?? throw new InvalidOperationException("Camera2 could not build a repeating request.");
            _session.SetRepeatingRequest(request, null, _imageHandler);
            _running = true;
            await WaitForFirstFramesAsync(cancellationToken).ConfigureAwait(false);
            Diagnostic?.Invoke(this,
                $"Camera2 physical pair active: {string.Join(" + ", _sources.Select(source => source.Capability.Name))}");
        }
        catch (Exception exception)
        {
            foreach (var source in _sources.Where(source => !source.FirstFrame.Task.IsCompletedSuccessfully))
            {
                SourceStatusChanged?.Invoke(source.Capability.Id, $"NO FRAMES · {exception.Message}", true);
            }
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        _running = false;
        try
        {
            _session?.StopRepeating();
        }
        catch (CameraAccessException)
        {
        }

        _session?.Close();
        _session?.Dispose();
        _session = null;
        _device?.Close();
        _device?.Dispose();
        _device = null;
        _deviceCallback?.Dispose();
        _deviceCallback = null;
        _sessionCallback?.Dispose();
        _sessionCallback = null;

        foreach (var listener in _listeners)
        {
            listener.Dispose();
        }
        _listeners.Clear();
        foreach (var reader in _readers)
        {
            reader.Close();
            reader.Dispose();
        }
        _readers.Clear();
        foreach (var surface in _previewSurfaces)
        {
            surface.Dispose();
        }
        _previewSurfaces.Clear();

        _imageHandler?.Dispose();
        _imageHandler = null;
        if (_imageThread is not null)
        {
            _imageThread.QuitSafely();
            _imageThread.Dispose();
            _imageThread = null;
        }

        foreach (var source in _sources)
        {
            source.Reset();
        }
    }

    private async Task WaitForTexturesAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = await MainThread.InvokeOnMainThreadAsync(
                () => _sources.Count == 2
                    && _sources.All(source => source.Preview.IsAvailable
                        && source.Preview.SurfaceTexture is not null));
            if (ready)
            {
                return;
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        throw new System.TimeoutException("Physical camera preview surfaces did not become available.");
    }

    private void PrepareOutputs()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            throw new PlatformNotSupportedException("Physical camera outputs require Android 9 or newer.");
        }

        _imageThread = new HandlerThread("mobilelpr-physical-images");
        _imageThread.Start();
        _imageHandler = new Handler(_imageThread.Looper
            ?? throw new InvalidOperationException("Camera2 image thread has no looper."));

        foreach (var source in _sources)
        {
            var texture = source.Preview.SurfaceTexture
                ?? throw new InvalidOperationException($"{source.Capability.Name} preview surface is unavailable.");
            // Preview stays modest; the YUV reader carries the user-selected analysis resolution.
            texture.SetDefaultBufferSize(1280, 720);
            _previewSurfaces.Add(new Surface(texture));

            var reader = ImageReader.NewInstance(
                source.Profile.Resolution.Width,
                source.Profile.Resolution.Height,
                ImageFormatType.Yuv420888,
                2) ?? throw new InvalidOperationException(
                    $"Could not create {source.Profile.Resolution} YUV reader for {source.Capability.Name}.");
            var listener = new ImageAvailableListener(source, FrameAvailable, FrameObserved, ReportDiagnostic);
            reader.SetOnImageAvailableListener(listener, _imageHandler);
            _readers.Add(reader);
            _listeners.Add(listener);
        }
    }

    private async Task WaitForFirstFramesAsync(CancellationToken cancellationToken)
    {
        var allFrames = Task.WhenAll(_sources.Select(source => source.FirstFrame.Task));
        try
        {
            await allFrames.WaitAsync(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
        }
        catch (System.TimeoutException)
        {
            var missing = _sources
                .Where(source => !source.FirstFrame.Task.IsCompletedSuccessfully)
                .Select(source => source.Capability.Name)
                .ToArray();
            throw new System.TimeoutException(
                $"No frames arrived from {string.Join(" and ", missing)}. The selected dual-camera/resolution combination is not usable.");
        }
    }

    private async Task<CameraDevice> OpenCameraAsync(
        CameraManager manager,
        string logicalCameraId,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<CameraDevice>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _deviceCallback = new DeviceStateCallback(
            camera => completion.TrySetResult(camera),
            camera =>
            {
                camera.Close();
                completion.TrySetException(new InvalidOperationException("Logical camera disconnected."));
            },
            (camera, error) =>
            {
                camera.Close();
                completion.TrySetException(new InvalidOperationException($"Camera2 open failed: {error}."));
            });
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        await MainThread.InvokeOnMainThreadAsync(() =>
            manager.OpenCamera(logicalCameraId, _deviceCallback, null));
        return await completion.Task.ConfigureAwait(false);
    }

    [SupportedOSPlatform("android28.0")]
    private async Task<CameraCaptureSession> CreateSessionAsync(
        CameraDevice camera,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<CameraCaptureSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionCallback = new CaptureSessionStateCallback(
            camera => completion.TrySetResult(camera),
            session =>
            {
                session.Close();
                completion.TrySetException(new InvalidOperationException(
                    "Camera2 rejected the selected physical preview/YUV stream combination. Try 1080p."));
            });

        var outputs = new List<OutputConfiguration>();
        for (var index = 0; index < _sources.Count; index++)
        {
            var physicalId = _sources[index].Capability.PhysicalCameraId
                ?? throw new InvalidOperationException("Physical camera ID is missing.");
            var previewOutput = new OutputConfiguration(_previewSurfaces[index]);
            previewOutput.SetPhysicalCameraId(physicalId);
            outputs.Add(previewOutput);
            var analysisOutput = new OutputConfiguration(_readers[index].Surface!);
            analysisOutput.SetPhysicalCameraId(physicalId);
            outputs.Add(analysisOutput);
        }

        var executor = _context.MainExecutor
            ?? throw new InvalidOperationException("Android returned no main executor.");
        var configuration = new SessionConfiguration(
            (int)SessionType.Regular,
            outputs,
            executor,
            _sessionCallback);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        await MainThread.InvokeOnMainThreadAsync(() => camera.CreateCaptureSession(configuration));
        return await completion.Task.ConfigureAwait(false);
    }

    private void ApplyPhysicalZoom(
        CaptureRequest.Builder builder,
        CameraManager manager,
        ConfiguredSource source)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30) || source.Profile.Zoom <= 1f)
        {
            return;
        }

        var physicalId = source.Capability.PhysicalCameraId!;
        var characteristics = manager.GetCameraCharacteristics(physicalId);
        var range = characteristics.Get(CameraCharacteristics.ControlZoomRatioRange) as global::Android.Util.Range;
        var maximum = range?.Upper is Java.Lang.Float upper ? upper.FloatValue() : source.Capability.MaximumZoom;
        var zoom = System.Math.Clamp(source.Profile.Zoom, 1f, System.Math.Max(1f, maximum));
        try
        {
#pragma warning disable CA1422
            builder.SetPhysicalCameraKey(
                CaptureRequest.ControlZoomRatio,
                new Java.Lang.Float(zoom),
                physicalId);
#pragma warning restore CA1422
            Diagnostic?.Invoke(this,
                $"{source.Capability.Name}: independent physical zoom {zoom:0.0}× accepted by request builder.");
        }
        catch (Exception exception) when (exception is Java.Lang.IllegalArgumentException
            or Java.Lang.IllegalStateException)
        {
            Diagnostic?.Invoke(this,
                $"{source.Capability.Name}: independent physical zoom {zoom:0.0}× is not supported by this device; using optical 1.0×. Android said: {exception.Message}");
        }
    }

    private void FrameAvailable(string sourceId, Yuv420Frame frame)
    {
        SourceFramesAvailable?.Invoke(this, new DriveFrameCountEventArgs(1));
        _submitFrame(sourceId, frame);
    }

    private void FrameObserved(ConfiguredSource source, int width, int height)
    {
        if (!source.FirstFrame.TrySetResult(true))
        {
            return;
        }

        var message = $"LIVE · analysis {width}×{height}";
        SourceStatusChanged?.Invoke(source.Capability.Id, message, false);
        Diagnostic?.Invoke(this,
            $"{source.Capability.Name}: actual analysis {width}x{height}, requested {source.Profile.Resolution}.");
    }

    private void ReportDiagnostic(string message) => Diagnostic?.Invoke(this, message);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
    }

    private sealed class ConfiguredSource(
        DriveSourceCapability capability,
        DriveSourceProfile profile,
        TextureView preview,
        Func<int> framesPerSecond)
    {
        public DriveSourceCapability Capability { get; } = capability;
        public DriveSourceProfile Profile { get; } = profile;
        public TextureView Preview { get; } = preview;
        public Func<int> FramesPerSecond { get; } = framesPerSecond;
        public FrameRateGate Gate { get; } = new(timestampFrequency: 1000);
        public long Sequence;
        public int ReportedResolution;
        public TaskCompletionSource<bool> FirstFrame { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Reset()
        {
            Gate.Reset();
            Interlocked.Exchange(ref ReportedResolution, 0);
            FirstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class ImageAvailableListener(
        ConfiguredSource source,
        Action<string, Yuv420Frame> frameAvailable,
        Action<ConfiguredSource, int, int> frameObserved,
        Action<string> diagnostic) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader)
        {
            global::Android.Media.Image? image = null;
            try
            {
                image = reader?.AcquireLatestImage();
                if (image is null)
                {
                    return;
                }

                frameObserved(source, image.Width, image.Height);
                if (!source.Gate.TryAcquire(System.Environment.TickCount64, source.FramesPerSecond()))
                {
                    return;
                }

                var planes = image.GetPlanes();
                if (planes is null || planes.Length != 3)
                {
                    return;
                }

                var y = CopyPlane(planes[0]);
                var u = CopyPlane(planes[1]);
                var v = CopyPlane(planes[2]);
                try
                {
                    var frame = new Yuv420Frame(
                        Interlocked.Increment(ref source.Sequence),
                        DateTimeOffset.UtcNow,
                        image.Width,
                        image.Height,
                        90,
                        y.Owner!, y.Length, planes[0].RowStride, planes[0].PixelStride,
                        u.Owner!, u.Length, planes[1].RowStride, planes[1].PixelStride,
                        v.Owner!, v.Length, planes[2].RowStride, planes[2].PixelStride);
                    y = default;
                    u = default;
                    v = default;
                    frameAvailable(source.Capability.Id, frame);
                }
                finally
                {
                    y.Owner?.Dispose();
                    u.Owner?.Dispose();
                    v.Owner?.Dispose();
                }
            }
            catch (Exception exception)
            {
                diagnostic($"{source.Capability.Name}: Camera2 frame ingestion failed: {exception.Message}");
            }
            finally
            {
                // ImageReader counts an image as acquired until Close() is called. Dispose() alone
                // is not sufficiently deterministic through the Android binding at 4K frame rates.
                image?.Close();
                image?.Dispose();
            }
        }

        private static PlaneCopy CopyPlane(global::Android.Media.Image.Plane plane)
        {
            var buffer = plane.Buffer?.Duplicate()
                ?? throw new InvalidDataException("Camera2 plane has no buffer.");
            var length = buffer.Remaining();
            var owner = new PooledByteOwner(length);
            try
            {
                buffer.Get(owner.Array, 0, length);
                return new PlaneCopy(owner, length);
            }
            catch
            {
                owner.Dispose();
                throw;
            }
            finally
            {
                buffer.Dispose();
            }
        }
    }

    private readonly record struct PlaneCopy(IMemoryOwner<byte>? Owner, int Length);

    private sealed class PooledByteOwner(int minimumLength) : IMemoryOwner<byte>
    {
        private byte[]? _array = ArrayPool<byte>.Shared.Rent(minimumLength);
        public byte[] Array => _array ?? throw new ObjectDisposedException(nameof(PooledByteOwner));
        public Memory<byte> Memory => Array;

        public void Dispose()
        {
            var array = Interlocked.Exchange(ref _array, null);
            if (array is not null)
            {
                ArrayPool<byte>.Shared.Return(array);
            }
        }
    }

    private sealed class DeviceStateCallback(
        Action<CameraDevice> opened,
        Action<CameraDevice> disconnected,
        Action<CameraDevice, CameraError> failed) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => opened(camera);
        public override void OnDisconnected(CameraDevice camera) => disconnected(camera);
        public override void OnError(CameraDevice camera, CameraError error) => failed(camera, error);
    }

    private sealed class CaptureSessionStateCallback(
        Action<CameraCaptureSession> configured,
        Action<CameraCaptureSession> failed) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) => configured(session);
        public override void OnConfigureFailed(CameraCaptureSession session) => failed(session);
    }
}
