#if ANDROID
using PlatformCameraPreviewHost = DeveMobileLPR.App.Platforms.Android.Camera.AndroidCameraPreviewHost;
#elif WINDOWS
using PlatformCameraPreviewHost = DeveMobileLPR.App.Platforms.Windows.Camera.WindowsCameraPreviewHost;
#endif
using DeveMobileLPR.App.Controls;
using Microsoft.Maui.Handlers;

namespace DeveMobileLPR.App.Handlers;

/// <summary>Shared handler contract; platform partials only adapt the native host and input factory.</summary>
internal partial class CameraPreviewHandler : ViewHandler<CameraPreview, PlatformCameraPreviewHost>
{
    public static readonly IPropertyMapper<CameraPreview, CameraPreviewHandler> Mapper =
        new PropertyMapper<CameraPreview, CameraPreviewHandler>(ViewHandler.ViewMapper)
        {
            [nameof(CameraPreview.IsMultiSource)] = MapPresentationMode,
            [nameof(CameraPreview.IsNetworkStream)] = MapPresentationMode
        };

    public CameraPreviewHandler() : base(Mapper)
    {
    }

    protected override PlatformCameraPreviewHost CreatePlatformView() => CreatePlatformViewCore();

    protected override void ConnectHandler(PlatformCameraPreviewHost platformView)
    {
        base.ConnectHandler(platformView);
        ConnectPlatformView(platformView);
        UpdatePresentationMode(VirtualView);
    }

    protected override void DisconnectHandler(PlatformCameraPreviewHost platformView)
    {
        DisconnectPlatformView(platformView);
        VirtualView.ReportInputGeneration(0);
        VirtualView.ReportSourceViewports([]);
        base.DisconnectHandler(platformView);
    }

    private static void MapPresentationMode(CameraPreviewHandler handler, CameraPreview view) =>
        handler.UpdatePresentationMode(view);

    private partial PlatformCameraPreviewHost CreatePlatformViewCore();
    private partial void ConnectPlatformView(PlatformCameraPreviewHost platformView);
    private partial void DisconnectPlatformView(PlatformCameraPreviewHost platformView);
    private partial void UpdatePresentationMode(CameraPreview view);
}
