using System.Buffers;
using Android.Content;
using Android.Util;
using AndroidX.Camera.Core;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using AndroidX.Lifecycle;
using AndroidX.Camera.Core.ResolutionSelector;
using DeveMobileLPR.Imaging;
using Google.Common.Util.Concurrent;
using Java.Util.Concurrent;

namespace DeveMobileLPR.AndroidApp.Camera;

internal sealed record CameraChoice(string Id, string Name);

internal sealed class CameraXFrameSource : Java.Lang.Object, ImageAnalysis.IAnalyzer, IDisposable
{
    private static readonly global::Android.Util.Size RequestedAnalysisResolution = new(3840, 2160);
    private readonly Context _context;
    private readonly ILifecycleOwner _lifecycleOwner;
    private readonly PreviewView _previewView;
    private readonly Action<Yuv420Frame> _onFrame;
    private readonly IExecutorService _analysisExecutor = Executors.NewSingleThreadExecutor()
        ?? throw new InvalidOperationException("Could not create the camera analysis executor.");
    private ProcessCameraProvider? _provider;
    private ICamera? _camera;
    private long _sequence;
    private long _nextCaptureTicks;
    private int _reportedResolution;
    private bool _disposed;
    private bool _running;
    private string _selectedCameraId = "rear";
    private IReadOnlyList<CameraChoice> _cameraChoices = [new("rear", "Rear cameras · automatic lens")];

    public CameraXFrameSource(Context context, ILifecycleOwner lifecycleOwner, PreviewView previewView, Action<Yuv420Frame> onFrame)
    {
        _context = context;
        _lifecycleOwner = lifecycleOwner;
        _previewView = previewView;
        _onFrame = onFrame;
    }

    public event EventHandler<string>? Diagnostic;
    public event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged;
    public IReadOnlyList<CameraChoice> CameraChoices => _cameraChoices;
    public string SelectedCameraId => _selectedCameraId;

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_provider is not null)
        {
            _running = true;
            BindCamera(_provider);
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var future = ProcessCameraProvider.GetInstance(_context);
        future.AddListener(new Java.Lang.Runnable(() =>
        {
            try
            {
                _provider = (ProcessCameraProvider?)future.Get()
                    ?? throw new InvalidOperationException("CameraX returned no camera provider.");
                _running = true;
                RefreshCameraChoices(_provider);
                BindCamera(_provider);
                completion.TrySetResult();
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
        _provider?.UnbindAll();
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
        var state = _camera?.CameraInfo?.ZoomState?.Value as IZoomState;
        if (state is null)
        {
            return;
        }

        _camera!.CameraControl?.SetZoomRatio(Math.Clamp(zoomRatio, state.MinZoomRatio, state.MaxZoomRatio));
    }

    private void BindCamera(ProcessCameraProvider provider)
    {
        provider.UnbindAll();
        var preview = new Preview.Builder().Build()
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
        Diagnostic?.Invoke(this, $"Camera active; requested analysis resolution {RequestedAnalysisResolution.Width}x{RequestedAnalysisResolution.Height}.");
    }

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
            var now = Environment.TickCount64;
            if (now < Interlocked.Read(ref _nextCaptureTicks))
            {
                return;
            }

            Interlocked.Exchange(ref _nextCaptureTicks, now + 250);
            if (Interlocked.Exchange(ref _reportedResolution, 1) == 0)
            {
                Diagnostic?.Invoke(this, $"Camera analysis resolution: {image.Width}x{image.Height}.");
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
            Diagnostic?.Invoke(this, $"Frame ingestion failed: {exception.Message}");
        }
        finally
        {
            image.Close();
        }
    }

    private static PlaneCopy CopyPlane(IImageProxyPlaneProxy plane)
    {
        var buffer = plane.Buffer?.Duplicate() ?? throw new InvalidDataException("Camera plane has no buffer.");
        var length = buffer.Remaining();
        var owner = MemoryPool<byte>.Shared.Rent(length);
        var temporary = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            buffer.Get(temporary, 0, length);
            temporary.AsSpan(0, length).CopyTo(owner.Memory.Span);
            return new PlaneCopy(owner, length);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temporary);
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
        _provider?.UnbindAll();
        _analysisExecutor.Shutdown();
        base.Dispose();
    }

    private readonly record struct PlaneCopy(IMemoryOwner<byte>? Owner, int Length);
}
