using System.Buffers;
using Android.Content;
using Android.Hardware.Display;
using Android.Runtime;
using Android.Util;
using Android.Views;
using AndroidX.Camera.Core;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using AndroidX.Lifecycle;
using AndroidX.Camera.Core.ResolutionSelector;
using DeveMobileLPR.Application;
using DeveMobileLPR.Imaging;
using Google.Common.Util.Concurrent;
using Java.Util.Concurrent;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class CameraXFrameSource : Java.Lang.Object, ImageAnalysis.IAnalyzer, IDriveFrameSourceTelemetry, IDisposable
{
    private const string LogTag = "DeveMobileLPR.Camera";
    private const int ZoomStateRetryCount = 8;
    private const long ZoomStateRetryDelayMilliseconds = 50;
    private static readonly global::Android.Util.Size RequestedAnalysisResolution = new(3840, 2160);
    private readonly Context _context;
    private readonly ILifecycleOwner _lifecycleOwner;
    private readonly PreviewView _previewView;
    private readonly Func<int> _recognitionFramesPerSecond;
    private readonly Action<Yuv420Frame> _onFrame;
    private readonly FrameRateGate _recognitionFrameGate = new(timestampFrequency: 1000);
    private readonly IExecutorService _analysisExecutor = Executors.NewSingleThreadExecutor()
        ?? throw new InvalidOperationException("Could not create the camera analysis executor.");
    private readonly object _providerTaskGate = new();
    private Task<ProcessCameraProvider>? _providerTask;
    private ProcessCameraProvider? _provider;
    private ICamera? _camera;
    private Preview? _preview;
    private ImageAnalysis? _analysis;
    private readonly DisplayManager? _displayManager;
    private readonly CameraXDisplayRotationListener _displayRotationListener;
    private long _sequence;
    private int _reportedResolution;
    private bool _disposed;
    private bool _cameraChoicesPrepared;
    private bool _running;
    private int _targetRotation = -1;
    private int _zoomRequestVersion;
    private float _requestedZoomRatio = 1f;
    private string _selectedCameraId = "rear";
    private IReadOnlyList<CameraChoice> _cameraChoices = [new("rear", "Rear cameras · automatic lens")];

    public CameraXFrameSource(
        Context context,
        ILifecycleOwner lifecycleOwner,
        PreviewView previewView,
        Func<int> recognitionFramesPerSecond,
        Action<Yuv420Frame> onFrame)
    {
        _context = context;
        _lifecycleOwner = lifecycleOwner;
        _previewView = previewView;
        _recognitionFramesPerSecond = recognitionFramesPerSecond;
        _onFrame = onFrame;
        _displayManager = context.GetSystemService(Context.DisplayService) as DisplayManager;
        _displayRotationListener = new CameraXDisplayRotationListener(previewView, UpdateTargetRotation);
        _displayManager?.RegisterDisplayListener(_displayRotationListener, null);
    }

    public event EventHandler<string>? Diagnostic;
    public event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged;
    public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
    public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented
    {
        add { }
        remove { }
    }
    public bool ReportsPreviewFrames => false;
    public IReadOnlyList<CameraChoice> CameraChoices => _cameraChoices;
    public string SelectedCameraId => _selectedCameraId;

    /// <summary>
    /// Discovers available lenses without binding preview or analysis use cases.
    /// </summary>
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cameraChoicesPrepared)
        {
            return;
        }

        var provider = await GetProviderAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _provider = provider;
            if (!_cameraChoicesPrepared)
            {
                RefreshCameraChoices(provider);
                _cameraChoicesPrepared = true;
            }
        });
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _recognitionFrameGate.Reset();
        await PrepareAsync(cancellationToken).ConfigureAwait(false);
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _running = true;
            try
            {
                BindCamera(_provider ?? throw new InvalidOperationException("CameraX is not prepared."));
            }
            catch
            {
                _running = false;
                throw;
            }
        });
    }

    private Task<ProcessCameraProvider> GetProviderAsync()
    {
        lock (_providerTaskGate)
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

    public void Stop()
    {
        _running = false;
        _recognitionFrameGate.Reset();
        Interlocked.Increment(ref _zoomRequestVersion);
        _provider?.UnbindAll();
        _camera = null;
        _preview = null;
        _analysis = null;
    }

    public void SelectCamera(string cameraId)
    {
        if (cameraId is not ("rear" or "front") || cameraId == _selectedCameraId)
        {
            return;
        }

        _selectedCameraId = cameraId;
        if (_running && _provider is not null)
        {
            BindCamera(_provider);
        }
    }

    public void SetZoom(float zoomRatio)
    {
        Volatile.Write(ref _requestedZoomRatio, Math.Max(1f, zoomRatio));
        var requestVersion = Interlocked.Increment(ref _zoomRequestVersion);
        if (_running)
        {
            ScheduleZoom(requestVersion, 0);
        }
    }

    private void ScheduleZoom(int requestVersion, int attempt)
    {
        var action = new Java.Lang.Runnable(() => ApplyRequestedZoom(requestVersion, attempt));
        if (attempt == 0)
        {
            _previewView.Post(action);
        }
        else
        {
            _previewView.PostDelayed(action, ZoomStateRetryDelayMilliseconds);
        }
    }

    private void ApplyRequestedZoom(int requestVersion, int attempt)
    {
        if (!_running || requestVersion != Volatile.Read(ref _zoomRequestVersion))
        {
            return;
        }

        var camera = _camera;
        var control = camera?.CameraControl;
        var state = GetZoomState(camera);
        if (camera is null || control is null || state is null)
        {
            if (attempt < ZoomStateRetryCount)
            {
                ScheduleZoom(requestVersion, attempt + 1);
            }
            else
            {
                ReportDiagnostic("Camera zoom is unavailable for the selected camera.");
            }
            return;
        }

        var requested = Volatile.Read(ref _requestedZoomRatio);
        var target = Math.Clamp(requested, state.MinZoomRatio, state.MaxZoomRatio);
        var future = control.SetZoomRatio(target)
            ?? throw new InvalidOperationException("CameraX returned no zoom operation.");
        future.AddListener(new Java.Lang.Runnable(() =>
        {
            if (requestVersion != Volatile.Read(ref _zoomRequestVersion))
            {
                return;
            }

            try
            {
                future.Get();
                var applied = GetZoomState(camera);
                ReportDiagnostic(
                    $"Camera zoom applied: {applied?.ZoomRatio ?? target:0.0}× " +
                    $"(requested {requested:0.0}×; supported {state.MinZoomRatio:0.0}–{state.MaxZoomRatio:0.0}×).");
            }
            catch (Exception exception)
            {
                ReportDiagnostic($"Camera zoom failed: {exception.GetBaseException().Message}");
            }
        }), ContextCompat.GetMainExecutor(_context));
    }

    private static IZoomState? GetZoomState(ICamera? camera)
    {
        var value = camera?.CameraInfo?.ZoomState?.Value;
        if (value is IZoomState state)
        {
            return state;
        }

        return value?.JavaCast<IZoomState>();
    }

    private void BindCamera(ProcessCameraProvider provider)
    {
        provider.UnbindAll();
        var targetRotation = GetTargetRotation();
        var preview = new Preview.Builder()
            .SetTargetRotation(targetRotation)!
            .Build()
            ?? throw new InvalidOperationException("CameraX could not create the preview use case.");
        preview.SetSurfaceProvider(
            ContextCompat.GetMainExecutor(_context),
            _previewView.SurfaceProvider ?? throw new InvalidOperationException("The preview surface is unavailable."));

        var resolutionStrategy = new ResolutionStrategy(
            RequestedAnalysisResolution,
            ResolutionStrategy.FallbackRuleClosestHigherThenLower);
        var resolutionSelector = new ResolutionSelector.Builder()
            .SetResolutionStrategy(resolutionStrategy)!
            .Build() ?? throw new InvalidOperationException("CameraX could not create a resolution selector.");
        var analysis = new ImageAnalysis.Builder()
            .SetResolutionSelector(resolutionSelector)!
            .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)!
            .SetOutputImageFormat(ImageAnalysis.OutputImageFormatYuv420888)!
            .SetTargetRotation(targetRotation)!
            .Build() ?? throw new InvalidOperationException("CameraX could not create the analysis use case.");
        analysis.SetAnalyzer(_analysisExecutor, this);
        var selector = _selectedCameraId == "front"
            ? CameraSelector.DefaultFrontCamera
            : CameraSelector.DefaultBackCamera;
        _camera = provider.BindToLifecycle(
            _lifecycleOwner,
            selector ?? throw new InvalidOperationException("The selected camera is unavailable."),
            preview,
            analysis);
        _preview = preview;
        _analysis = analysis;
        _targetRotation = targetRotation;
        var zoomRequestVersion = Interlocked.Increment(ref _zoomRequestVersion);
        ScheduleZoom(zoomRequestVersion, 0);
        ReportDiagnostic($"Camera active; requested analysis resolution {RequestedAnalysisResolution.Width}x{RequestedAnalysisResolution.Height}; target rotation {RotationName(targetRotation)}.");
    }

    private int GetTargetRotation() => (int)(_previewView.Display?.Rotation ?? SurfaceOrientation.Rotation0);

    private void UpdateTargetRotation()
    {
        if (!_running || _preview is null || _analysis is null)
        {
            return;
        }

        var targetRotation = GetTargetRotation();
        if (targetRotation == _targetRotation)
        {
            return;
        }

        _preview.TargetRotation = targetRotation;
        _analysis.TargetRotation = targetRotation;
        _targetRotation = targetRotation;
        Interlocked.Exchange(ref _reportedResolution, 0);
        ReportDiagnostic($"Camera target rotation updated to {RotationName(targetRotation)}.");
    }

    private static string RotationName(int rotation) => rotation switch
    {
        (int)SurfaceOrientation.Rotation0 => "0°",
        (int)SurfaceOrientation.Rotation90 => "90°",
        (int)SurfaceOrientation.Rotation180 => "180°",
        (int)SurfaceOrientation.Rotation270 => "270°",
        _ => rotation.ToString()
    };

    private void RefreshCameraChoices(ProcessCameraProvider provider)
    {
        var choices = new List<CameraChoice>();
        if (CameraSelector.DefaultBackCamera is { } back && provider.HasCamera(back))
        {
            choices.Add(new CameraChoice("rear", "Rear cameras · automatic lens"));
        }
        if (CameraSelector.DefaultFrontCamera is { } front && provider.HasCamera(front))
        {
            choices.Add(new CameraChoice("front", "Front camera"));
        }
        if (choices.Count == 0)
        {
            choices.Add(new CameraChoice("rear", "Rear camera"));
        }
        _cameraChoices = choices;
        if (!_cameraChoices.Any(choice => choice.Id == _selectedCameraId))
        {
            _selectedCameraId = _cameraChoices[0].Id;
        }
        CameraChoicesChanged?.Invoke(this, _cameraChoices);
    }

    public void Analyze(IImageProxy? image)
    {
        if (image is null)
        {
            return;
        }

        try
        {
            if (!_running)
            {
                return;
            }

            SourceFramesAvailable?.Invoke(this, new DriveFrameCountEventArgs(1));
            if (!_recognitionFrameGate.TryAcquire(
                    Environment.TickCount64,
                    _recognitionFramesPerSecond()))
            {
                return;
            }
            if (Interlocked.Exchange(ref _reportedResolution, 1) == 0)
            {
                ReportDiagnostic($"Camera analysis resolution: {image.Width}x{image.Height}; frame rotation {image.ImageInfo?.RotationDegrees ?? 0}°.");
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
                    Interlocked.Increment(ref _sequence),
                    DateTimeOffset.UtcNow,
                    image.Width,
                    image.Height,
                    image.ImageInfo?.RotationDegrees ?? 0,
                    y.Owner ?? throw new InvalidDataException("Y plane ownership was lost."), y.Length, planes[0].RowStride, planes[0].PixelStride,
                    u.Owner ?? throw new InvalidDataException("U plane ownership was lost."), u.Length, planes[1].RowStride, planes[1].PixelStride,
                    v.Owner ?? throw new InvalidDataException("V plane ownership was lost."), v.Length, planes[2].RowStride, planes[2].PixelStride);
                y = default;
                u = default;
                v = default;
                _onFrame(frame);
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
            ReportDiagnostic($"Frame ingestion failed: {exception.Message}");
        }
        finally
        {
            image.Close();
        }
    }

    private void ReportDiagnostic(string message)
    {
        Log.Info(LogTag, message);
        Diagnostic?.Invoke(this, message);
    }

    private static PlaneCopy CopyPlane(IImageProxyPlaneProxy plane)
    {
        var buffer = plane.Buffer?.Duplicate() ?? throw new InvalidDataException("Camera plane has no buffer.");
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

    public new void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _zoomRequestVersion);
        _provider?.UnbindAll();
        _displayManager?.UnregisterDisplayListener(_displayRotationListener);
        _analysisExecutor.Shutdown();
        base.Dispose();
    }

    private readonly record struct PlaneCopy(IMemoryOwner<byte>? Owner, int Length);

    private sealed class PooledByteOwner : IMemoryOwner<byte>
    {
        private byte[]? _array;

        public PooledByteOwner(int minimumLength)
        {
            _array = ArrayPool<byte>.Shared.Rent(minimumLength);
        }

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
