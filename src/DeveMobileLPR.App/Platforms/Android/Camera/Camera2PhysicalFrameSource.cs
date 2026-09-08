using System.Buffers;
using System.Diagnostics;
using System.Runtime.Versioning;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Views;
using DeveMobileLPR.Application;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using Java.Lang;
using AndroidSize = Android.Util.Size;
using Exception = System.Exception;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class Camera2PhysicalFrameSource : IDisposable
{
    private readonly Context _context;
    private readonly Func<int> _recognitionFramesPerSecond;
    private readonly Func<string, Yuv420Frame, bool> _submitFrame;
    private readonly List<ConfiguredSource> _sources = [];
    private readonly List<ImageReader> _readers = [];
    private readonly List<Surface> _previewSurfaces = [];
    private readonly List<ImageAvailableListener> _listeners = [];
    private readonly List<HandlerThread> _imageThreads = [];
    private readonly List<Handler> _imageHandlers = [];
    private CameraDevice? _device;
    private CameraCaptureSession? _session;
    private CameraDevice.StateCallback? _deviceCallback;
    private CameraCaptureSession.StateCallback? _sessionCallback;
    private CameraCaptureSession.CaptureCallback? _captureCallback;
    private CancellationTokenSource? _healthCancellation;
    private Task? _healthTask;
    private bool _running;
    private bool _usesNativePreview;
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
    public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented;
    public event Action<string, string, bool>? SourceStatusChanged;
    public event Action<string>? SourceStalled;

    public void Configure(
        IReadOnlyList<(DriveSourceCapability Capability, DriveSourceProfile Profile, PhysicalCameraPreviewView Preview)> sources)
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

        foreach (var source in _sources)
        {
            source.DetachPreviewHeartbeat();
        }
        _sources.Clear();
        _sources.AddRange(sources.Select(source => new ConfiguredSource(
            source.Capability,
            source.Profile,
            source.Preview,
            _recognitionFramesPerSecond)));
        foreach (var source in _sources)
        {
            source.AttachPreviewHeartbeat(() => PreviewFramePresented(source));
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            return;
        }

        var manager = _context.GetSystemService(Context.CameraService) as CameraManager
            ?? throw new InvalidOperationException("Android returned no CameraManager.");

        if (!OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            throw new PlatformNotSupportedException("Physical camera outputs require Android 9 or newer.");
        }
        try
        {
            var nativeTexturesReady = await WaitForNativePreviewTexturesAsync(cancellationToken)
                .ConfigureAwait(false);
            PrepareOutputs(manager, nativeTexturesReady);
            Diagnostic?.Invoke(this,
                $"Starting Camera2 physical session with two YUV analysis outputs" +
                (nativeTexturesReady ? " and two native preview outputs: " : ": ") +
                string.Join(" + ", _sources.Select(source =>
                    $"{source.Capability.Name} at {source.Profile.Resolution}, {source.Profile.Zoom:0.0}x")));
            foreach (var source in _sources)
            {
                SourceStatusChanged?.Invoke(source.Capability.Id, "WAITING FOR CAMERA FRAMES", false);
            }
            var logicalId = _sources[0].Capability.LogicalCameraId
                ?? throw new InvalidOperationException("Logical camera ID is missing.");
            _device = await OpenCameraAsync(manager, logicalId, cancellationToken).ConfigureAwait(false);
            _usesNativePreview = nativeTexturesReady;
            if (_usesNativePreview)
            {
                try
                {
                    _session = await CreateSessionAsync(_device, includeNativePreviews: true, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is NotSupportedException
                    or PhysicalSessionConfigurationException)
                {
                    Diagnostic?.Invoke(this,
                        $"Native physical previews are unavailable for this stream combination; " +
                        $"falling back to shared YUV previews. Android said: {exception.Message}");
                    _sessionCallback?.Dispose();
                    _sessionCallback = null;
                    _usesNativePreview = false;
                }
            }
            _session ??= await CreateSessionAsync(_device, includeNativePreviews: false, cancellationToken)
                .ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                foreach (var source in _sources)
                {
                    source.UseNativePreview = _usesNativePreview;
                    source.Preview.UseNativePreview(_usesNativePreview);
                }
            });
            var physicalIds = _sources
                .Select(source => source.Capability.PhysicalCameraId!)
                .ToArray();
            var requestBuilder = _device.CreateCaptureRequest(CameraTemplate.Preview, physicalIds)
                ?? throw new InvalidOperationException("Camera2 could not create a repeating request.");
            foreach (var reader in _readers)
            {
                requestBuilder.AddTarget(reader.Surface!);
            }
            if (_usesNativePreview)
            {
                foreach (var surface in _previewSurfaces)
                {
                    requestBuilder.AddTarget(surface);
                }
            }
            foreach (var source in _sources)
            {
                ApplyPhysicalZoom(requestBuilder, manager, logicalId, source);
            }

            var request = requestBuilder.Build()
                ?? throw new InvalidOperationException("Camera2 could not build a repeating request.");
            _captureCallback = new RepeatingCaptureCallback(message => Diagnostic?.Invoke(this, message));
            _session.SetRepeatingRequest(request, _captureCallback, _imageHandlers[0]);
            _running = true;
            await WaitForFirstFramesAsync(cancellationToken).ConfigureAwait(false);
            Diagnostic?.Invoke(this,
                $"Camera2 physical pair active: {string.Join(" + ", _sources.Select(source => source.Capability.Name))}" +
                (_usesNativePreview
                    ? " · two 4K YUV outputs + two native preview outputs"
                    : " · two YUV outputs total · software-preview fallback shares analysis frames"));
            StartHealthMonitor();
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
        var elapsed = Stopwatch.StartNew();
        Diagnostic?.Invoke(this, "Stopping Camera2 physical streams…");
        _running = false;
        _healthCancellation?.Cancel();
        _healthCancellation?.Dispose();
        _healthCancellation = null;
        _healthTask = null;
        // CameraCaptureSession.Close() already stops repeating requests. Calling
        // StopRepeating() first adds a synchronous HAL round-trip which can wait forever
        // when one physical stream has stalled—the exact state we most need to escape.
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
        _captureCallback?.Dispose();
        _captureCallback = null;

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
        foreach (var handler in _imageHandlers)
        {
            handler.Dispose();
        }
        _imageHandlers.Clear();
        foreach (var thread in _imageThreads)
        {
            thread.QuitSafely();
            thread.Dispose();
        }
        _imageThreads.Clear();

        foreach (var source in _sources)
        {
            source.Reset();
        }
        Diagnostic?.Invoke(this,
            $"Camera2 physical streams stopped in {elapsed.Elapsed.TotalMilliseconds:0} ms.");
    }

    private void PrepareOutputs(CameraManager manager, bool includeNativePreviews)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            throw new PlatformNotSupportedException("Physical camera outputs require Android 9 or newer.");
        }

        foreach (var source in _sources)
        {
            source.Orientation = GetOrientation(manager, source);
            Diagnostic?.Invoke(this,
                $"{source.Capability.Name}: geometry · sensor {source.Orientation.SensorOrientationDegrees}° · " +
                $"display {source.Orientation.DisplayRotationDegrees}° · " +
                $"{(source.Orientation.PreviewMirrored ? " mirrored" : string.Empty)} · " +
                $"AI rotation {source.Orientation.AiRotationDegrees}° · " +
                $"analysis YUV {source.Profile.Resolution} · mode Fit.");

            if (includeNativePreviews)
            {
                var texture = source.Preview.Native.SurfaceTexture
                    ?? throw new InvalidOperationException(
                        $"{source.Capability.Name} native preview surface is unavailable.");
                var previewSize = SelectPreviewSize(manager, source);
                texture.SetDefaultBufferSize(previewSize.Width, previewSize.Height);
                var quarterTurn = source.Orientation.AiRotationDegrees % 180 != 0;
                var uprightWidth = quarterTurn ? previewSize.Height : previewSize.Width;
                var uprightHeight = quarterTurn ? previewSize.Width : previewSize.Height;
                source.Preview.Native.ConfigureBuffer(
                    previewSize.Width,
                    previewSize.Height,
                    uprightWidth,
                    uprightHeight,
                    source.Orientation.PreviewRotationDegrees,
                    AspectScaleMode.Fit,
                    source.Orientation.PreviewMirrored);
                _previewSurfaces.Add(new Surface(texture));
                Diagnostic?.Invoke(this,
                    $"{source.Capability.Name}: native preview buffer {previewSize.Width}x{previewSize.Height}.");
            }

            var reader = ImageReader.NewInstance(
                source.Profile.Resolution.Width,
                source.Profile.Resolution.Height,
                ImageFormatType.Yuv420888,
                2) ?? throw new InvalidOperationException(
                    $"Could not create {source.Profile.Resolution} YUV reader for {source.Capability.Name}.");
            var listener = new ImageAvailableListener(source, FrameAvailable, FrameObserved, ReportDiagnostic);
            var thread = new HandlerThread($"mobilelpr-physical-{source.Capability.PhysicalCameraId}-images");
            thread.Start();
            var handler = new Handler(thread.Looper
                ?? throw new InvalidOperationException("Camera2 image thread has no looper."));
            reader.SetOnImageAvailableListener(listener, handler);
            _readers.Add(reader);
            _listeners.Add(listener);
            _imageThreads.Add(thread);
            _imageHandlers.Add(handler);
        }
    }

    private async Task<bool> WaitForNativePreviewTexturesAsync(CancellationToken cancellationToken)
    {
        var timeoutAt = System.Environment.TickCount64 + 2000;
        while (System.Environment.TickCount64 < timeoutAt)
        {
            var ready = await MainThread.InvokeOnMainThreadAsync(() =>
                _sources.All(source => source.Preview.Native.IsAvailable));
            if (ready)
            {
                return true;
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        Diagnostic?.Invoke(this,
            "Native physical preview textures were not ready within 2 seconds; using software previews.");
        return false;
    }

    private static AndroidSize SelectPreviewSize(CameraManager manager, ConfiguredSource source)
    {
        var physicalId = source.Capability.PhysicalCameraId
            ?? throw new InvalidOperationException("Physical camera ID is missing.");
        var characteristics = manager.GetCameraCharacteristics(physicalId);
        var map = characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap)
            as StreamConfigurationMap
            ?? throw new InvalidOperationException($"Physical ID {physicalId} reports no stream map.");
        var outputSizes = map.GetOutputSizes(Class.FromType(typeof(SurfaceTexture)))
            ?? throw new InvalidOperationException($"Physical ID {physicalId} reports no preview sizes.");
        var targetAspect = source.Profile.Resolution.Width / (double)source.Profile.Resolution.Height;
        const long targetArea = 1280L * 720;
        return outputSizes
            .OrderBy(size => System.Math.Abs(size.Width / (double)size.Height - targetAspect))
            .ThenBy(size => size.Width <= 1280 ? 0 : 1)
            .ThenBy(size => System.Math.Abs((long)size.Width * size.Height - targetArea))
            .First();
    }

    private static CameraOrientationContract GetOrientation(
        CameraManager manager,
        ConfiguredSource source)
    {
        var physicalId = source.Capability.PhysicalCameraId
            ?? throw new InvalidOperationException("Physical camera ID is missing.");
        var characteristics = manager.GetCameraCharacteristics(physicalId);
        var sensorOrientation = (characteristics.Get(CameraCharacteristics.SensorOrientation)
            as Java.Lang.Integer)?.IntValue() ?? 0;
        var displayRotation = source.Preview.Display?.Rotation ?? SurfaceOrientation.Rotation0;
        var displayDegrees = displayRotation switch
        {
            SurfaceOrientation.Rotation90 => 90,
            SurfaceOrientation.Rotation180 => 180,
            SurfaceOrientation.Rotation270 => 270,
            _ => 0
        };
        return CameraOrientationContract.Create(
            sensorOrientation,
            displayDegrees,
            source.Capability.InferredRole == InferredLensRole.Front);
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
        bool includeNativePreviews,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<CameraCaptureSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionCallback = new CaptureSessionStateCallback(
            camera => completion.TrySetResult(camera),
            session =>
            {
                session.Close();
                completion.TrySetException(new PhysicalSessionConfigurationException(
                    includeNativePreviews
                        ? "Camera2 rejected the two physical YUV plus two native preview outputs."
                        : "Camera2 rejected the selected two-physical-YUV stream combination. Try 1080p."));
            });

        var outputs = new List<OutputConfiguration>();
        for (var index = 0; index < _sources.Count; index++)
        {
            var physicalId = _sources[index].Capability.PhysicalCameraId
                ?? throw new InvalidOperationException("Physical camera ID is missing.");
            var analysisOutput = new OutputConfiguration(_readers[index].Surface!);
            analysisOutput.SetPhysicalCameraId(physicalId);
            outputs.Add(analysisOutput);
            if (includeNativePreviews)
            {
                var previewOutput = new OutputConfiguration(_previewSurfaces[index]);
                previewOutput.SetPhysicalCameraId(physicalId);
                outputs.Add(previewOutput);
            }
        }

        var executor = _context.MainExecutor
            ?? throw new InvalidOperationException("Android returned no main executor.");
        using var configuration = new SessionConfiguration(
            (int)SessionType.Regular,
            outputs,
            executor,
            _sessionCallback);
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29)
                && !camera.IsSessionConfigurationSupported(configuration))
            {
                throw new NotSupportedException(
                    includeNativePreviews
                        ? "Android reports that the four-output physical-camera session is unsupported."
                        : "Android reports that the selected two-physical-YUV configuration is unsupported. Try 1080p.");
            }
            Diagnostic?.Invoke(this, OperatingSystem.IsAndroidVersionAtLeast(29)
                ? $"Android reports that the {(includeNativePreviews ? "four-output native-preview" : "two-physical-YUV")} session configuration is supported."
                : "Android 9 cannot preflight this physical-camera configuration; session creation will verify it.");
        }
        catch (Java.Lang.UnsupportedOperationException)
        {
            Diagnostic?.Invoke(this,
                "Android cannot preflight this physical-camera configuration; session creation will verify it.");
        }
        try
        {
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            await MainThread.InvokeOnMainThreadAsync(() => camera.CreateCaptureSession(configuration));
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            foreach (var output in outputs)
            {
                output.Dispose();
            }
        }
    }

    [SupportedOSPlatform("android28.0")]
    private void ApplyPhysicalZoom(
        CaptureRequest.Builder builder,
        CameraManager manager,
        string logicalCameraId,
        ConfiguredSource source)
    {
        if (source.Profile.Zoom <= 1f)
        {
            return;
        }

        var physicalId = source.Capability.PhysicalCameraId!;
        var logicalCharacteristics = manager.GetCameraCharacteristics(logicalCameraId);
        var physicalKeys = logicalCharacteristics.AvailablePhysicalCameraRequestKeys;
#pragma warning disable CA1416
        var cropKeyAvailable = physicalKeys?.Any(key =>
            string.Equals(key?.Name, CaptureRequest.ScalerCropRegion?.Name, StringComparison.Ordinal)) == true;
        var zoomKeyAvailable = OperatingSystem.IsAndroidVersionAtLeast(30)
            && physicalKeys?.Any(key =>
                string.Equals(key?.Name, CaptureRequest.ControlZoomRatio?.Name, StringComparison.Ordinal)) == true;
#pragma warning restore CA1416
        var characteristics = manager.GetCameraCharacteristics(physicalId);
        var range = OperatingSystem.IsAndroidVersionAtLeast(30)
            ? characteristics.Get(CameraCharacteristics.ControlZoomRatioRange) as global::Android.Util.Range
            : null;
        var maximum = range?.Upper is Java.Lang.Float upper
            ? upper.FloatValue()
            : source.Capability.MaximumZoom;
        var zoom = System.Math.Clamp(source.Profile.Zoom, 1f, System.Math.Max(1f, maximum));
        try
        {
            if (cropKeyAvailable
                && characteristics.Get(CameraCharacteristics.SensorInfoActiveArraySize)
                    is global::Android.Graphics.Rect activeArray)
            {
                var cropWidth = System.Math.Max(2, (int)(activeArray.Width() / zoom)) & ~1;
                var cropHeight = System.Math.Max(2, (int)(activeArray.Height() / zoom)) & ~1;
                var left = activeArray.Left + (activeArray.Width() - cropWidth) / 2;
                var top = activeArray.Top + (activeArray.Height() - cropHeight) / 2;
                var crop = new global::Android.Graphics.Rect(left, top, left + cropWidth, top + cropHeight);
#pragma warning disable CA1422
                builder.SetPhysicalCameraKey(CaptureRequest.ScalerCropRegion!, crop, physicalId);
#pragma warning restore CA1422
                source.EffectiveZoom = zoom;
                Diagnostic?.Invoke(this,
                    $"{source.Capability.Name}: independent physical crop {zoom:0.0}× requested · " +
                    $"active [{activeArray.Left},{activeArray.Top},{activeArray.Right},{activeArray.Bottom}] · " +
                    $"crop [{crop.Left},{crop.Top},{crop.Right},{crop.Bottom}].");
                return;
            }
            if (!OperatingSystem.IsAndroidVersionAtLeast(30) || !zoomKeyAvailable)
            {
                source.EffectiveZoom = 1f;
                Diagnostic?.Invoke(this,
                    $"{source.Capability.Name}: this logical camera advertises neither physical crop nor physical zoom; using optical 1.0× instead of {source.Profile.Zoom:0.0}×.");
                return;
            }
#pragma warning disable CA1422
            builder.SetPhysicalCameraKey(
                CaptureRequest.ControlZoomRatio,
                new Java.Lang.Float(zoom),
                physicalId);
#pragma warning restore CA1422
            source.EffectiveZoom = zoom;
            Diagnostic?.Invoke(this,
                $"{source.Capability.Name}: independent physical zoom ratio {zoom:0.0}× requested.");
        }
        catch (Exception exception) when (exception is Java.Lang.IllegalArgumentException
            or Java.Lang.IllegalStateException)
        {
            source.EffectiveZoom = 1f;
            Diagnostic?.Invoke(this,
                $"{source.Capability.Name}: independent physical zoom {zoom:0.0}× is not supported by this device; using optical 1.0×. Android said: {exception.Message}");
        }
    }

    private void FrameAvailable(string sourceId, Yuv420Frame frame)
    {
        SourceFramesAvailable?.Invoke(this, new DriveFrameCountEventArgs(1));
        _submitFrame(sourceId, frame);
    }

    private void PreviewFramePresented(ConfiguredSource source)
    {
        if (!_running)
        {
            return;
        }
        source.MarkPreviewFrame();
        PreviewFramesPresented?.Invoke(this, new DriveFrameCountEventArgs(1));
    }

    private void StartHealthMonitor()
    {
        _healthCancellation?.Cancel();
        _healthCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        _healthCancellation = cancellation;
        var sources = _sources.ToArray();
        var now = System.Environment.TickCount64;
        foreach (var source in sources)
        {
            source.StartHealthMonitoring(now);
        }

        _healthTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!_running)
                    {
                        continue;
                    }
                    var current = System.Environment.TickCount64;
                    foreach (var source in sources)
                    {
                        var change = source.CheckHealth(current);
                        if (change is null)
                        {
                            continue;
                        }

                        Diagnostic?.Invoke(this, $"{source.Capability.Name}: {change.Value.Message}");
                        SourceStatusChanged?.Invoke(
                            source.Capability.Id,
                            change.Value.Status,
                            change.Value.IsError);
                        if (change.Value.IsError)
                        {
                            SourceStalled?.Invoke(source.Capability.Id);
                        }
                    }
                }
            }
            catch (System.OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Diagnostic?.Invoke(this, $"Camera health monitor failed: {exception.Message}");
            }
        });
    }

    private void FrameObserved(ConfiguredSource source, int width, int height)
    {
        if (!source.FirstFrame.TrySetResult(true))
        {
            return;
        }

        var message = $"LIVE · analysis {width}×{height} · zoom {source.EffectiveZoom:0.0}×";
        SourceStatusChanged?.Invoke(source.Capability.Id, message, false);
        Diagnostic?.Invoke(this,
            $"{source.Capability.Name}: actual analysis {width}x{height}, requested {source.Profile.Resolution}; " +
            $"AI rotation {source.Orientation.AiRotationDegrees}°; " +
            $"preview panel {source.Preview.Width}x{source.Preview.Height}; " +
            $"preview path {(_usesNativePreview ? "native SurfaceTexture" : "software YUV fallback")}.");
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
        foreach (var source in _sources)
        {
            source.DetachPreviewHeartbeat();
        }
    }

    private sealed class ConfiguredSource(
        DriveSourceCapability capability,
        DriveSourceProfile profile,
        PhysicalCameraPreviewView preview,
        Func<int> framesPerSecond)
    {
        public DriveSourceCapability Capability { get; } = capability;
        public DriveSourceProfile Profile { get; } = profile;
        public PhysicalCameraPreviewView Preview { get; } = preview;
        public Func<int> FramesPerSecond { get; } = framesPerSecond;
        public FrameRateGate AnalysisGate { get; } = new(timestampFrequency: 1000);
        public FrameRateGate PreviewGate { get; } = new(timestampFrequency: 1000);
        public long Sequence;
        public int ReportedResolution;
        public float EffectiveZoom { get; set; } = 1f;
        public bool UseNativePreview { get; set; }
        public CameraOrientationContract Orientation { get; set; }
        public TaskCompletionSource<bool> FirstFrame { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MarkAnalysisFrame() =>
            Interlocked.Exchange(ref LastAnalysisFrameTick, System.Environment.TickCount64);

        public void MarkPreviewFrame() =>
            Interlocked.Exchange(ref LastPreviewFrameTick, System.Environment.TickCount64);

        public void StartHealthMonitoring(long now)
        {
            Interlocked.Exchange(ref LastAnalysisFrameTick, now);
            Interlocked.Exchange(ref LastPreviewFrameTick, now);
            Interlocked.Exchange(ref HealthState, 0);
        }

        public HealthChange? CheckHealth(long now)
        {
            const long stalledAfterMilliseconds = 3000;
            var analysisAge = now - Interlocked.Read(ref LastAnalysisFrameTick);
            var previewAge = now - Interlocked.Read(ref LastPreviewFrameTick);
            var next = (analysisAge >= stalledAfterMilliseconds ? 1 : 0)
                | (previewAge >= stalledAfterMilliseconds ? 2 : 0);
            var previous = Interlocked.Exchange(ref HealthState, next);
            if (next == previous)
            {
                return null;
            }

            if (next == 0)
            {
                return new HealthChange("LIVE", false, "preview and analysis frames recovered.");
            }

            var stalled = next switch
            {
                1 => "ANALYSIS STALLED",
                2 => "PREVIEW STALLED",
                _ => "PREVIEW + ANALYSIS STALLED"
            };
            return new HealthChange(
                stalled,
                true,
                $"{stalled.ToLowerInvariant()} · last preview {previewAge / 1000d:0.0}s ago · " +
                $"last analysis {analysisAge / 1000d:0.0}s ago.");
        }

        public long LastAnalysisFrameTick;
        public long LastPreviewFrameTick;
        public int HealthState;
        private Action? _previewHeartbeat;

        public void AttachPreviewHeartbeat(Action heartbeat)
        {
            DetachPreviewHeartbeat();
            _previewHeartbeat = heartbeat;
            Preview.Native.FramePresented += heartbeat;
            Preview.Software.FramePresented += heartbeat;
        }

        public void DetachPreviewHeartbeat()
        {
            if (_previewHeartbeat is { } heartbeat)
            {
                Preview.Native.FramePresented -= heartbeat;
                Preview.Software.FramePresented -= heartbeat;
                _previewHeartbeat = null;
            }
        }

        public void Reset()
        {
            AnalysisGate.Reset();
            PreviewGate.Reset();
            Interlocked.Exchange(ref ReportedResolution, 0);
            EffectiveZoom = 1f;
            Interlocked.Exchange(ref HealthState, 0);
            FirstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private readonly record struct HealthChange(string Status, bool IsError, string Message);

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

                source.MarkAnalysisFrame();
                frameObserved(source, image.Width, image.Height);
                var now = System.Environment.TickCount64;
                var analyze = source.AnalysisGate.TryAcquire(now, source.FramesPerSecond());
                var preview = !source.UseNativePreview
                    && source.Preview.Software.CanAcceptFrame
                    && source.PreviewGate.TryAcquire(now, 8);
                if (!analyze && !preview)
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
                    if (preview)
                    {
                        try
                        {
                            source.Preview.Software.TryPresent(
                                image.Width,
                                image.Height,
                                source.Orientation.AiRotationDegrees,
                                source.Orientation.PreviewMirrored,
                                y.Owner!.Array,
                                y.Length,
                                planes[0].RowStride,
                                planes[0].PixelStride,
                                u.Owner!.Array,
                                u.Length,
                                planes[1].RowStride,
                                planes[1].PixelStride,
                                v.Owner!.Array,
                                v.Length,
                                planes[2].RowStride,
                                planes[2].PixelStride);
                        }
                        catch (Exception exception)
                        {
                            diagnostic($"{source.Capability.Name}: software preview failed: {exception.Message}");
                        }
                    }

                    if (analyze)
                    {
                        var frame = new Yuv420Frame(
                            Interlocked.Increment(ref source.Sequence),
                            DateTimeOffset.UtcNow,
                            image.Width,
                            image.Height,
                            source.Orientation.AiRotationDegrees,
                            y.Owner!, y.Length, planes[0].RowStride, planes[0].PixelStride,
                            u.Owner!, u.Length, planes[1].RowStride, planes[1].PixelStride,
                            v.Owner!, v.Length, planes[2].RowStride, planes[2].PixelStride);
                        y = default;
                        u = default;
                        v = default;
                        frameAvailable(source.Capability.Id, frame);
                    }
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

    private readonly record struct PlaneCopy(PooledByteOwner? Owner, int Length);

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

    private sealed class PhysicalSessionConfigurationException(string message) : Exception(message);

    private sealed class RepeatingCaptureCallback(Action<string> diagnostic)
        : CameraCaptureSession.CaptureCallback
    {
        public override void OnCaptureFailed(
            CameraCaptureSession session,
            CaptureRequest request,
            CaptureFailure failure) =>
            diagnostic(
                $"Camera2 repeating capture failed · reason {failure.Reason} · " +
                $"frame {failure.FrameNumber} · sequence {failure.SequenceId}.");

        public override void OnCaptureSequenceAborted(CameraCaptureSession session, int sequenceId) =>
            diagnostic($"Camera2 repeating capture sequence {sequenceId} was aborted.");
    }
}
