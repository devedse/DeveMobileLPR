using System.Buffers;
using Android.Content;
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

internal sealed class CameraXMultiFrameSource : IDisposable
{
    private readonly Context _context;
    private readonly ILifecycleOwner _lifecycleOwner;
    private readonly Func<int> _recognitionFramesPerSecond;
    private readonly Func<string, Yuv420Frame, bool> _submitFrame;
    private readonly object _providerGate = new();
    private readonly List<SourceBinding> _bindings = [];
    private Task<ProcessCameraProvider>? _providerTask;
    private ProcessCameraProvider? _provider;
    private bool _running;
    private bool _disposed;

    public CameraXMultiFrameSource(
        Context context,
        ILifecycleOwner lifecycleOwner,
        Func<int> recognitionFramesPerSecond,
        Func<string, Yuv420Frame, bool> submitFrame)
    {
        _context = context;
        _lifecycleOwner = lifecycleOwner;
        _recognitionFramesPerSecond = recognitionFramesPerSecond;
        _submitFrame = submitFrame;
    }

    public event EventHandler<string>? Diagnostic;
    public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;

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
            Bind(_provider ?? throw new InvalidOperationException("CameraX is unavailable."));
            _running = true;
        });
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
        Stop();
        ClearBindings();
    }

    private sealed class SourceBinding : Java.Lang.Object, ImageAnalysis.IAnalyzer
    {
        private readonly Func<int> _recognitionFramesPerSecond;
        private readonly Action<string, Yuv420Frame> _frameAvailable;
        private readonly Action<string> _diagnostic;
        private readonly FrameRateGate _frameGate = new(timestampFrequency: 1000);
        private readonly IExecutorService _executor = Executors.NewSingleThreadExecutor()
            ?? throw new InvalidOperationException("Could not create a camera analysis executor.");
        private long _sequence;
        private int _reportedResolution;

        public SourceBinding(
            DriveSourceCapability capability,
            DriveSourceProfile profile,
            PreviewView previewView,
            Func<int> recognitionFramesPerSecond,
            Action<string, Yuv420Frame> frameAvailable,
            Action<string> diagnostic)
        {
            Capability = capability;
            Profile = profile;
            PreviewView = previewView;
            _recognitionFramesPerSecond = recognitionFramesPerSecond;
            _frameAvailable = frameAvailable;
            _diagnostic = diagnostic;
            Selector = BuildSelector(capability);
        }

        public DriveSourceCapability Capability { get; }
        public DriveSourceProfile Profile { get; }
        public PreviewView PreviewView { get; }
        public CameraSelector Selector { get; }
        public Preview PreviewUseCase { get; private set; } = null!;
        public ImageAnalysis AnalysisUseCase { get; private set; } = null!;

        public void BuildUseCases(Context context)
        {
            var requested = new AndroidSize(Profile.Resolution.Width, Profile.Resolution.Height);
            var strategy = new ResolutionStrategy(
                requested,
                ResolutionStrategy.FallbackRuleClosestHigherThenLower);
            var resolutionSelector = new ResolutionSelector.Builder()
                .SetResolutionStrategy(strategy)!
                .Build() ?? throw new InvalidOperationException("Could not build a resolution selector.");

            PreviewUseCase = new Preview.Builder()
                .SetResolutionSelector(resolutionSelector)!
                .Build() ?? throw new InvalidOperationException("Could not build camera preview.");
            PreviewUseCase.SetSurfaceProvider(
                ContextCompat.GetMainExecutor(context),
                PreviewView.SurfaceProvider ?? throw new InvalidOperationException("Preview surface unavailable."));

            AnalysisUseCase = new ImageAnalysis.Builder()
                .SetResolutionSelector(resolutionSelector)!
                .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)!
                .SetOutputImageFormat(ImageAnalysis.OutputImageFormatYuv420888)!
                .Build() ?? throw new InvalidOperationException("Could not build camera analysis.");
            AnalysisUseCase.SetAnalyzer(_executor, this);
        }

        public void ApplyZoom(ICamera camera)
        {
            var state = camera.CameraInfo?.ZoomState?.Value as IZoomState;
            var target = state is null
                ? Math.Max(1f, Profile.Zoom)
                : Math.Clamp(Profile.Zoom, state.MinZoomRatio, state.MaxZoomRatio);
            var operation = camera.CameraControl?.SetZoomRatio(target);
            if (operation is not null)
            {
                operation.AddListener(
                    new Java.Lang.Runnable(() => _diagnostic(
                        $"{Capability.Name}: zoom {target:0.0}× requested.")),
                    ContextCompat.GetMainExecutor(PreviewView.Context));
            }
        }

        public void Analyze(IImageProxy? image)
        {
            if (image is null)
            {
                return;
            }

            try
            {
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
                        $"requested {Profile.Resolution}.");
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
            _frameGate.Reset();
            Interlocked.Exchange(ref _reportedResolution, 0);
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
}
