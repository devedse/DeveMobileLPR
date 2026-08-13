using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Views;
using Android.Widget;
using DeveMobileLPR.App.Controls;
using Java.Lang;
using Microsoft.Maui.Handlers;
using AndroidSize = Android.Util.Size;
using Color = Android.Graphics.Color;
using Exception = System.Exception;
using View = Android.Views.View;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class DualCameraPreviewHandler : ViewHandler<DualCameraPreview, LinearLayout>
{
    private const string LogicalRearCameraId = "0";

    public static readonly IPropertyMapper<DualCameraPreview, DualCameraPreviewHandler> Mapper =
        new PropertyMapper<DualCameraPreview, DualCameraPreviewHandler>(ViewHandler.ViewMapper)
        {
            [nameof(DualCameraPreview.IsActive)] = static (handler, view) => handler.UpdateActive(view.IsActive)
        };

    private readonly List<(string CameraId, TextureView View)> _previewViews = [];
    private readonly List<Surface> _surfaces = [];
    private readonly List<(string CameraId, AndroidSize Size)> _configuredOutputs = [];
    private CameraDevice? _cameraDevice;
    private CameraCaptureSession? _captureSession;
    private CameraDevice.StateCallback? _deviceCallback;
    private CameraCaptureSession.StateCallback? _sessionCallback;
    private int _operationVersion;

    public DualCameraPreviewHandler() : base(Mapper)
    {
    }

    protected override LinearLayout CreatePlatformView()
    {
        var context = MauiContext?.Context ?? throw new InvalidOperationException("Android context is unavailable.");
        return new LinearLayout(context)
        {
            Orientation = Orientation.Vertical
        };
    }

    protected override void ConnectHandler(LinearLayout platformView)
    {
        base.ConnectHandler(platformView);
        UpdateActive(VirtualView.IsActive);
    }

    protected override void DisconnectHandler(LinearLayout platformView)
    {
        Stop();
        base.DisconnectHandler(platformView);
    }

    private void UpdateActive(bool active)
    {
        if (!active)
        {
            Stop();
            return;
        }

        var version = Interlocked.Increment(ref _operationVersion);
        _ = StartAsync(version);
    }

    private async Task StartAsync(int version)
    {
        try
        {
            var cameraIds = VirtualView.GetCameraIds();
            if (cameraIds.Length is < 2 or > 4)
            {
                throw new InvalidOperationException($"Expected 2–4 physical camera IDs, got {cameraIds.Length}.");
            }

            VirtualView.ReportStatus(
                $"CAMERA2 SESSION STARTING\nLogical camera: {LogicalRearCameraId}\n" +
                $"Physical IDs: {string.Join(" + ", cameraIds)}\n" +
                $"Requested each: {VirtualView.RequestedWidth}×{VirtualView.RequestedHeight}");

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (version != _operationVersion || !VirtualView.IsActive)
                {
                    return;
                }

                StopCamera();
                BuildPreviewGrid(cameraIds);
            });
            await WaitForPreviewSurfacesAsync(version);

            if (version != _operationVersion || !VirtualView.IsActive)
            {
                return;
            }

            var context = MauiContext?.Context ?? throw new InvalidOperationException("Android context is unavailable.");
            var manager = context.GetSystemService(Context.CameraService) as CameraManager
                ?? throw new InvalidOperationException("Android returned no CameraManager.");

            BuildOutputSurfaces(manager, cameraIds);
            _cameraDevice = await OpenCameraAsync(manager, context, version);
            if (version != _operationVersion || !VirtualView.IsActive)
            {
                _cameraDevice.Close();
                return;
            }

            _captureSession = await CreateSessionAsync(_cameraDevice, context, cameraIds, version);
            var requestBuilder = _cameraDevice.CreateCaptureRequest(CameraTemplate.Preview)
                ?? throw new InvalidOperationException("Camera2 could not create a preview request.");
            foreach (var surface in _surfaces)
            {
                requestBuilder.AddTarget(surface);
            }

            var request = requestBuilder.Build()
                ?? throw new InvalidOperationException("Camera2 could not build the repeating request.");
            _captureSession.SetRepeatingRequest(request, null, null);
            VirtualView.ReportStatus(BuildSuccessStatus());
        }
        catch (Exception exception)
        {
            if (version != _operationVersion)
            {
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(StopCamera);
            VirtualView.ReportStatus(
                $"SESSION FAILED\n{exception.GetType().Name}: {exception.Message}\n\n" +
                "The selected physical-stream combination was rejected. Try fewer cameras or 1280×720.");
        }
    }

    private void BuildPreviewGrid(IReadOnlyList<string> cameraIds)
    {
        PlatformView.RemoveAllViews();
        _previewViews.Clear();
        var context = MauiContext?.Context ?? throw new InvalidOperationException("Android context is unavailable.");
        var rowCount = (cameraIds.Count + 1) / 2;

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
            PlatformView.AddView(row, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1));

            for (var column = 0; column < 2; column++)
            {
                var cameraIndex = (rowIndex * 2) + column;
                if (cameraIndex >= cameraIds.Count)
                {
                    row.AddView(new Space(context), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1));
                    continue;
                }

                var cameraId = cameraIds[cameraIndex];
                var frame = new FrameLayout(context);
                var texture = new TextureView(context);
                texture.SetBackgroundColor(Color.Black);
                frame.AddView(texture, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

                var label = new TextView(context)
                {
                    Text = $" physical ID {cameraId} ",
                    TextSize = 13
                };
                label.SetTextColor(Color.Rgb(245, 185, 66));
                label.SetBackgroundColor(Color.Argb(190, 11, 13, 16));
                label.SetPadding(8, 4, 8, 4);
                frame.AddView(label, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

                row.AddView(frame, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1));
                _previewViews.Add((cameraId, texture));
            }
        }
    }

    private async Task WaitForPreviewSurfacesAsync(int version)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var ready = await MainThread.InvokeOnMainThreadAsync(
                () => _previewViews.Count > 0 &&
                    _previewViews.All(item => item.View.IsAvailable && item.View.SurfaceTexture is not null));
            if (ready)
            {
                return;
            }

            if (version != _operationVersion || !VirtualView.IsActive)
            {
                throw new OperationCanceledException();
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Preview surfaces did not become available.");
    }

    private void BuildOutputSurfaces(CameraManager manager, IReadOnlyList<string> cameraIds)
    {
        foreach (var surface in _surfaces)
        {
            surface.Dispose();
        }

        _surfaces.Clear();
        _configuredOutputs.Clear();

        foreach (var cameraId in cameraIds)
        {
            var previewView = _previewViews.Single(item => item.CameraId == cameraId).View;
            var texture = previewView.SurfaceTexture
                ?? throw new InvalidOperationException($"Preview surface for physical ID {cameraId} is unavailable.");
            var size = SelectOutputSize(manager, cameraId, VirtualView.RequestedWidth, VirtualView.RequestedHeight);
            texture.SetDefaultBufferSize(size.Width, size.Height);
            _surfaces.Add(new Surface(texture));
            _configuredOutputs.Add((cameraId, size));
        }
    }

    private async Task<CameraDevice> OpenCameraAsync(
        CameraManager manager,
        Context context,
        int version)
    {
        var completion = new TaskCompletionSource<CameraDevice>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _deviceCallback = new DeviceStateCallback(
            camera => completion.TrySetResult(camera),
            camera =>
            {
                camera.Close();
                completion.TrySetException(new InvalidOperationException("Logical rear camera disconnected."));
            },
            (camera, error) =>
            {
                camera.Close();
                completion.TrySetException(new InvalidOperationException($"Camera2 open failed: {error}."));
            });

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (version == _operationVersion && VirtualView.IsActive)
            {
                manager.OpenCamera(LogicalRearCameraId, _deviceCallback, null);
            }
            else
            {
                completion.TrySetCanceled();
            }
        });
        return await completion.Task;
    }

    private async Task<CameraCaptureSession> CreateSessionAsync(
        CameraDevice camera,
        Context context,
        IReadOnlyList<string> cameraIds,
        int version)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            throw new PlatformNotSupportedException("Physical Camera2 outputs require Android 9 (API 28) or newer.");
        }

        var completion = new TaskCompletionSource<CameraCaptureSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionCallback = new CaptureSessionStateCallback(
            session => completion.TrySetResult(session),
            session =>
            {
                session.Close();
                completion.TrySetException(new InvalidOperationException(
                    "Camera2 rejected this physical output combination."));
            });

        var outputs = new List<OutputConfiguration>();
        for (var index = 0; index < _surfaces.Count; index++)
        {
            var output = new OutputConfiguration(_surfaces[index]);
            output.SetPhysicalCameraId(cameraIds[index]);
            outputs.Add(output);
        }

        var executor = context.MainExecutor
            ?? throw new InvalidOperationException("Android returned no main executor.");
        var configuration = new SessionConfiguration(
            (int)SessionType.Regular,
            outputs,
            executor,
            _sessionCallback);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (version == _operationVersion && VirtualView.IsActive)
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(28))
                {
                    throw new PlatformNotSupportedException("Physical Camera2 outputs require Android 9 (API 28) or newer.");
                }

                camera.CreateCaptureSession(configuration);
            }
            else
            {
                completion.TrySetCanceled();
            }
        });
        return await completion.Task;
    }

    private static AndroidSize SelectOutputSize(
        CameraManager manager,
        string cameraId,
        int requestedWidth,
        int requestedHeight)
    {
        var characteristics = manager.GetCameraCharacteristics(cameraId);
        var map = characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap)
            as StreamConfigurationMap
            ?? throw new InvalidOperationException($"Physical ID {cameraId} reports no stream configuration map.");
        var outputSizes = map.GetOutputSizes(Class.FromType(typeof(SurfaceTexture)))
            ?? throw new InvalidOperationException($"Physical ID {cameraId} reports no preview sizes.");
        var exact = outputSizes.FirstOrDefault(
            size => size.Width == requestedWidth && size.Height == requestedHeight);
        if (exact is not null)
        {
            return exact;
        }

        var targetArea = (long)requestedWidth * requestedHeight;
        return outputSizes
            .OrderBy(size => size.Width >= requestedWidth && size.Height >= requestedHeight ? 0 : 1)
            .ThenBy(size => System.Math.Abs(((long)size.Width * size.Height) - targetArea))
            .First();
    }

    private string BuildSuccessStatus()
    {
        var lines = new List<string>
        {
            "SESSION CONFIGURED",
            $"Logical camera: {LogicalRearCameraId}",
            $"Physical streams: {_configuredOutputs.Count}",
            $"Requested each: {VirtualView.RequestedWidth}×{VirtualView.RequestedHeight}"
        };
        lines.AddRange(_configuredOutputs.Select(
            output => $"ID {output.CameraId}: {output.Size.Width}×{output.Size.Height}"));
        lines.Add(string.Empty);
        lines.Add("Success only counts if every labelled panel contains a different, live view.");
        lines.Add("This proves preview surfaces, not simultaneous YUV analysis.");
        return string.Join('\n', lines);
    }

    private void Stop()
    {
        Interlocked.Increment(ref _operationVersion);
        StopCamera();
    }

    private void StopCamera()
    {
        try
        {
            _captureSession?.StopRepeating();
        }
        catch (CameraAccessException)
        {
        }

        _captureSession?.Close();
        _captureSession?.Dispose();
        _captureSession = null;
        _cameraDevice?.Close();
        _cameraDevice?.Dispose();
        _cameraDevice = null;
        _deviceCallback?.Dispose();
        _deviceCallback = null;
        _sessionCallback?.Dispose();
        _sessionCallback = null;

        foreach (var surface in _surfaces)
        {
            surface.Dispose();
        }

        _surfaces.Clear();
        _configuredOutputs.Clear();
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
