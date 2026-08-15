using System.Buffers;
using Android.Content;
using Android.Hardware.Display;
using Android.Runtime;
using Android.Views;
using AndroidX.Camera.Core;
using AndroidX.Camera.Core.ResolutionSelector;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using AndroidX.Lifecycle;
using DeveMobileLPR.Application;
using DeveMobileLPR.Imaging;
using Java.Util;
using Java.Util.Concurrent;
using AndroidSize = Android.Util.Size;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class CameraXIntegratedFrameSource : IDisposable
{
    private readonly Context _context;
    private readonly ILifecycleOwner _lifecycleOwner;
    private readonly Func<int> _recognitionFramesPerSecond;
    private readonly Func<string, Yuv420Frame, bool> _submitFrame;
    private readonly object _providerGate = new();
    private readonly List<SourceBinding> _bindings = [];
    private Task<ProcessCameraProvider>? _providerTask;
    private ProcessCameraProvider? _provider;
    private readonly DisplayManager? _displayManager;
    private readonly DisplayRotationListener _displayRotationListener;
    private bool _running;
    private bool _disposed;

    public CameraXIntegratedFrameSource(
        Context context,
        ILifecycleOwner lifecycleOwner,
        Func<int> recognitionFramesPerSecond,
        Func<string, Yuv420Frame, bool> submitFrame)
    {
        _context = context;
        _lifecycleOwner = lifecycleOwner;
        _recognitionFramesPerSecond = recognitionFramesPerSecond;
        _submitFrame = submitFrame;
        _displayManager = context.GetSystemService(Context.DisplayService) as DisplayManager;
        _displayRotationListener = new DisplayRotationListener(UpdateTargetRotations);
        _displayManager?.RegisterDisplayListener(_displayRotationListener, null);
    }

    public event EventHandler<string>? Diagnostic;
    public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
    public event Action<string, string, bool>? SourceStatusChanged;

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _provider = await GetProviderAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Configure(
        IReadOnlyList<(DriveSourceCapability Capability, DriveSourceProfile Profile, PreviewView Preview)> sources)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            throw new InvalidOperationException("Stop camera capture before changing its configuration.");
        }

        if (sources.Count is < 1 or > 2)
        {
            throw new NotSupportedException(
                "Android CameraX supports one or two simultaneous integrated cameras.");
        }

        ClearBindings();
        foreach (var source in sources)
        {
            _bindings.Add(new SourceBinding(
                source.Capability,
                source.Profile,
                source.Preview,
                _recognitionFramesPerSecond,
                FrameAvailable,
                FrameObserved,
                message => Diagnostic?.Invoke(this, message)));
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await PrepareAsync(cancellationToken).ConfigureAwait(false);
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var binding in _bindings)
            {
                SourceStatusChanged?.Invoke(binding.Capability.Id, "WAITING FOR CAMERA FRAMES", false);
            }
            Bind(_provider ?? throw new InvalidOperationException("CameraX is unavailable."));
            _running = true;
        });
        try
        {
            await Task.WhenAll(_bindings.Select(binding => binding.FirstFrame.Task))
                .WaitAsync(TimeSpan.FromSeconds(6), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (System.TimeoutException)
        {
            var missing = _bindings.Where(binding => !binding.FirstFrame.Task.IsCompletedSuccessfully).ToArray();
            foreach (var binding in missing)
            {
                SourceStatusChanged?.Invoke(binding.Capability.Id, "NO ANALYSIS FRAMES", true);
            }
            Stop();
            throw new System.TimeoutException(
                $"No frames arrived from {string.Join(" and ", missing.Select(binding => binding.Capability.Name))}.");
        }
    }

    public void Stop()
    {
        _running = false;
        _provider?.UnbindAll();
        foreach (var binding in _bindings)
        {
            binding.Reset();
        }
    }

    public void SetZoom(string sourceId, float zoomRatio)
    {
        var binding = _bindings.FirstOrDefault(item => item.Capability.Id == sourceId);
        binding?.SetZoom(zoomRatio);
    }

    private void UpdateTargetRotations()
    {
        // A display callback can already be queued when the preview handler starts teardown.
        // Snapshot first so re-entrant MAUI/CameraX callbacks cannot invalidate List<T>'s
        // enumerator while ClearBindings removes the live bindings.
        foreach (var binding in _bindings.ToArray())
        {
            binding.UpdateTargetRotation();
        }
    }

    private void Bind(ProcessCameraProvider provider)
    {
        provider.UnbindAll();
        foreach (var binding in _bindings)
        {
            binding.BuildUseCases(_context);
        }

        if (_bindings.Count == 1)
        {
            var binding = _bindings[0];
            var camera = provider.BindToLifecycle(
                _lifecycleOwner,
                binding.Selector,
                binding.PreviewUseCase,
                binding.AnalysisUseCase);
            binding.ApplyZoom(camera);
        }
        else
        {
            var configs = new JavaList<ConcurrentCamera.SingleCameraConfig>();
            foreach (var binding in _bindings)
            {
                var group = new UseCaseGroup.Builder()
                    .AddUseCase(binding.PreviewUseCase)!
                    .AddUseCase(binding.AnalysisUseCase)!
                    .Build() ?? throw new InvalidOperationException("Could not build a concurrent use-case group.");
                configs.Add(new ConcurrentCamera.SingleCameraConfig(
                    binding.Selector,
                    group,
                    _lifecycleOwner));
            }

            var concurrent = provider.BindToLifecycle(configs);
            var cameras = concurrent.Cameras
                ?? throw new InvalidOperationException("CameraX returned no concurrent cameras.");
            if (cameras.Count < _bindings.Count)
            {
                throw new InvalidOperationException("CameraX returned fewer cameras than requested.");
            }
            for (var index = 0; index < _bindings.Count; index++)
            {
                _bindings[index].ApplyZoom(cameras[index]);
            }
        }

        Diagnostic?.Invoke(
            this,
            $"Camera active · {string.Join(" + ", _bindings.Select(binding => binding.Capability.Name))}");
    }

    private void FrameAvailable(string sourceId, Yuv420Frame frame)
    {
        SourceFramesAvailable?.Invoke(this, new DriveFrameCountEventArgs(1));
        _submitFrame(sourceId, frame);
    }

    private void FrameObserved(SourceBinding binding, int width, int height)
    {
        if (!binding.FirstFrame.TrySetResult(true))
        {
            return;
        }
        SourceStatusChanged?.Invoke(binding.Capability.Id, $"LIVE · analysis {width}×{height}", false);
    }

    private Task<ProcessCameraProvider> GetProviderAsync()
    {
        lock (_providerGate)
        {
            return _providerTask ??= CreateProviderTask();
        }
    }

    private Task<ProcessCameraProvider> CreateProviderTask()
    {
        var completion = new TaskCompletionSource<ProcessCameraProvider>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var future = ProcessCameraProvider.GetInstance(_context);
        future.AddListener(new Java.Lang.Runnable(() =>
        {
            try
            {
                completion.TrySetResult(
                    (ProcessCameraProvider?)future.Get()
                    ?? throw new InvalidOperationException("CameraX returned no camera provider."));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }), ContextCompat.GetMainExecutor(_context));
        return completion.Task;
    }

    private void ClearBindings()
    {
        foreach (var binding in _bindings)
        {
            binding.Dispose();
        }
        _bindings.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _displayManager?.UnregisterDisplayListener(_displayRotationListener);
        _displayRotationListener.Dispose();
        Stop();
        ClearBindings();
    }

    private sealed class SourceBinding : Java.Lang.Object, ImageAnalysis.IAnalyzer
    {
        private readonly Func<int> _recognitionFramesPerSecond;
        private readonly Action<string, Yuv420Frame> _frameAvailable;
        private readonly Action<SourceBinding, int, int> _frameObserved;
        private readonly Action<string> _diagnostic;
        private readonly FrameRateGate _frameGate = new(timestampFrequency: 1000);
        private readonly IExecutorService _executor = Executors.NewSingleThreadExecutor()
            ?? throw new InvalidOperationException("Could not create a camera analysis executor.");
        private long _sequence;
        private int _reportedResolution;
        private int _targetRotation = -1;
        private float _requestedZoom;
        private ICamera? _camera;

        public SourceBinding(
            DriveSourceCapability capability,
            DriveSourceProfile profile,
            PreviewView previewView,
            Func<int> recognitionFramesPerSecond,
            Action<string, Yuv420Frame> frameAvailable,
            Action<SourceBinding, int, int> frameObserved,
            Action<string> diagnostic)
        {
            Capability = capability;
            Profile = profile;
            _requestedZoom = profile.Zoom;
            PreviewView = previewView;
            _recognitionFramesPerSecond = recognitionFramesPerSecond;
            _frameAvailable = frameAvailable;
            _frameObserved = frameObserved;
            _diagnostic = diagnostic;
            Selector = BuildSelector(capability);
        }

        public DriveSourceCapability Capability { get; }
        public DriveSourceProfile Profile { get; }
        public PreviewView PreviewView { get; }
        public CameraSelector Selector { get; }
        public Preview PreviewUseCase { get; private set; } = null!;
        public ImageAnalysis AnalysisUseCase { get; private set; } = null!;
        public TaskCompletionSource<bool> FirstFrame { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BuildUseCases(Context context)
        {
            var targetRotation = GetTargetRotation();
            var requested = new AndroidSize(Profile.Resolution.Width, Profile.Resolution.Height);
            var strategy = new ResolutionStrategy(
                requested,
                ResolutionStrategy.FallbackRuleClosestHigherThenLower);
            var resolutionSelector = new ResolutionSelector.Builder()
                .SetResolutionStrategy(strategy)!
                .Build() ?? throw new InvalidOperationException("Could not build a resolution selector.");

            PreviewUseCase = new Preview.Builder()
                .SetResolutionSelector(resolutionSelector)!
                .SetTargetRotation(targetRotation)!
                .Build() ?? throw new InvalidOperationException("Could not build camera preview.");
            PreviewUseCase.SetSurfaceProvider(
                ContextCompat.GetMainExecutor(context),
                PreviewView.SurfaceProvider ?? throw new InvalidOperationException("Preview surface unavailable."));

            AnalysisUseCase = new ImageAnalysis.Builder()
                .SetResolutionSelector(resolutionSelector)!
                .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)!
                .SetOutputImageFormat(ImageAnalysis.OutputImageFormatYuv420888)!
                .SetTargetRotation(targetRotation)!
                .Build() ?? throw new InvalidOperationException("Could not build camera analysis.");
            AnalysisUseCase.SetAnalyzer(_executor, this);
            _targetRotation = targetRotation;
        }

        public void ApplyZoom(ICamera camera)
        {
            _camera = camera;
            var state = camera.CameraInfo?.ZoomState?.Value as IZoomState;
            var target = state is null
                ? Math.Max(1f, _requestedZoom)
                : Math.Clamp(_requestedZoom, state.MinZoomRatio, state.MaxZoomRatio);
            var operation = camera.CameraControl?.SetZoomRatio(target);
            if (operation is not null)
            {
                operation.AddListener(
                    new Java.Lang.Runnable(() => _diagnostic(
                        $"{Capability.Name}: zoom {target:0.0}× requested.")),
                    ContextCompat.GetMainExecutor(PreviewView.Context));
            }
        }

        public void SetZoom(float zoomRatio)
        {
            _requestedZoom = Math.Max(1f, zoomRatio);
            if (_camera is { } camera)
            {
                PreviewView.Post(new Java.Lang.Runnable(() => ApplyZoom(camera)));
            }
        }

        public void UpdateTargetRotation()
        {
            var targetRotation = GetTargetRotation();
            if (_targetRotation == targetRotation || PreviewUseCase is null || AnalysisUseCase is null)
            {
                return;
            }

            PreviewUseCase.TargetRotation = targetRotation;
            AnalysisUseCase.TargetRotation = targetRotation;
            _targetRotation = targetRotation;
            Interlocked.Exchange(ref _reportedResolution, 0);
            _diagnostic($"{Capability.Name}: target rotation updated to {RotationName(targetRotation)}.");
        }

        private int GetTargetRotation() =>
            (int)(PreviewView.Display?.Rotation ?? SurfaceOrientation.Rotation0);

        private static string RotationName(int rotation) => rotation switch
        {
            (int)SurfaceOrientation.Rotation0 => "0°",
            (int)SurfaceOrientation.Rotation90 => "90°",
            (int)SurfaceOrientation.Rotation180 => "180°",
            (int)SurfaceOrientation.Rotation270 => "270°",
            _ => rotation.ToString()
        };

        public void Analyze(IImageProxy? image)
        {
            if (image is null)
            {
                return;
            }

            try
            {
                _frameObserved(this, image.Width, image.Height);
                if (!_frameGate.TryAcquire(Environment.TickCount64, _recognitionFramesPerSecond()))
                {
                    return;
                }

                var planes = image.GetPlanes();
                if (planes is null || planes.Length != 3)
                {
                    return;
                }

                if (Interlocked.Exchange(ref _reportedResolution, 1) == 0)
                {
                    _diagnostic(
                        $"{Capability.Name}: actual analysis {image.Width}×{image.Height}, " +
                        $"requested {Profile.Resolution}; AI rotation {image.ImageInfo?.RotationDegrees ?? 0}°.");
                }

                var y = CopyPlane(planes[0]);
                var u = CopyPlane(planes[1]);
                var v = CopyPlane(planes[2]);
                try
                {
                    var frame = new Yuv420Frame(
                        Interlocked.Increment(ref _sequence),
                        DateTimeOffset.UtcNow,
                        image.Width,
                        image.Height,
                        image.ImageInfo?.RotationDegrees ?? 0,
                        y.Owner!, y.Length, planes[0].RowStride, planes[0].PixelStride,
                        u.Owner!, u.Length, planes[1].RowStride, planes[1].PixelStride,
                        v.Owner!, v.Length, planes[2].RowStride, planes[2].PixelStride);
                    y = default;
                    u = default;
                    v = default;
                    _frameAvailable(Capability.Id, frame);
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
                _diagnostic($"{Capability.Name}: frame ingestion failed: {exception.Message}");
            }
            finally
            {
                image.Close();
            }
        }

        public void Reset()
        {
            _camera = null;
            _frameGate.Reset();
            Interlocked.Exchange(ref _reportedResolution, 0);
            FirstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _executor.Shutdown();
            }
            base.Dispose(disposing);
        }

        private static CameraSelector BuildSelector(DriveSourceCapability capability)
        {
            if (capability.Id == "front")
            {
                return CameraSelector.DefaultFrontCamera
                    ?? throw new InvalidOperationException("Front camera is unavailable.");
            }
            if (capability.Id == "rear")
            {
                return CameraSelector.DefaultBackCamera
                    ?? throw new InvalidOperationException("Rear camera is unavailable.");
            }

            var builder = new CameraSelector.Builder();
            builder.RequireLensFacing(
                capability.InferredRole == InferredLensRole.Front
                    ? CameraSelector.LensFacingFront
                    : CameraSelector.LensFacingBack);
            builder.SetPhysicalCameraId(
                capability.PhysicalCameraId
                    ?? throw new InvalidOperationException("Physical camera ID is missing."));
            return builder.Build()
                ?? throw new InvalidOperationException($"Could not select {capability.Name}.");
        }

        private static PlaneCopy CopyPlane(IImageProxyPlaneProxy plane)
        {
            var buffer = plane.Buffer?.Duplicate()
                ?? throw new InvalidDataException("Camera plane has no buffer.");
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
    }

    private sealed class DisplayRotationListener(Action changed)
        : Java.Lang.Object, DisplayManager.IDisplayListener
    {
        public void OnDisplayAdded(int displayId) { }
        public void OnDisplayRemoved(int displayId) { }
        public void OnDisplayChanged(int displayId) =>
            MainThread.BeginInvokeOnMainThread(changed);
    }
}
