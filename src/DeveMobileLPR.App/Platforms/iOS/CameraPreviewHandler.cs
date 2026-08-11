using AVFoundation;
using CoreAnimation;
using CoreGraphics;
using DeveMobileLPR.App.Controls;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;
using DeveMobileLPR.Geometry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;
using UIKit;

namespace DeveMobileLPR.App;

internal sealed class CameraPreviewHandler : ViewHandler<CameraPreview, IosCameraPreviewView>
{
    private IosDriveFrameSource? _source;
    private DriveCoordinator? _coordinator;

    public static readonly IPropertyMapper<CameraPreview, CameraPreviewHandler> Mapper =
        new PropertyMapper<CameraPreview, CameraPreviewHandler>(ViewHandler.ViewMapper);

    public CameraPreviewHandler() : base(Mapper) { }

    protected override IosCameraPreviewView CreatePlatformView()
    {
        _coordinator = MauiContext!.Services.GetRequiredService<DriveCoordinator>();
        var settings = MauiContext.Services.GetRequiredService<AppSettings>();
        var view = new IosCameraPreviewView();
        _source = new IosDriveFrameSource(
            view,
            () => settings.RecognitionFramesPerSecond,
            () => _coordinator.HasPendingRecognitionFrame,
            frame => _coordinator.SubmitFrame(frame),
            settings.NetworkStreamUrl);
        _coordinator.AttachCamera(_source);
        return view;
    }

    protected override void ConnectHandler(IosCameraPreviewView platformView)
    {
        base.ConnectHandler(platformView);
        VirtualView.CameraScaleMode = AspectScaleMode.Fill;
        VirtualView.StreamScaleMode = AspectScaleMode.Fit;
    }

    protected override void DisconnectHandler(IosCameraPreviewView platformView)
    {
        if (_source is { } source)
        {
            _coordinator?.DetachCamera(source);
            _source = null;
            _ = DisposeAsync(source);
        }
        _coordinator = null;
        base.DisconnectHandler(platformView);
    }

    private static async Task DisposeAsync(IosDriveFrameSource source)
    {
        try { await source.DisposeAsync(); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"iOS camera cleanup failed: {exception}"); }
    }
}

internal sealed class IosCameraPreviewView : UIView
{
    private CALayer? _preview;

    public IosCameraPreviewView() => BackgroundColor = UIColor.FromRGB(11, 13, 16);

    public void Attach(AVCaptureSession session)
    {
        var preview = AVCaptureVideoPreviewLayer.FromSession(session);
        preview.VideoGravity = AVLayerVideoGravity.ResizeAspectFill;
        Attach(preview);
    }

    public void Attach(AVPlayer player)
    {
        var preview = AVPlayerLayer.FromPlayer(player);
        preview.VideoGravity = AVLayerVideoGravity.ResizeAspect;
        Attach(preview);
    }

    private void Attach(CALayer preview)
    {
        _preview?.RemoveFromSuperLayer();
        _preview?.Dispose();
        _preview = preview;
        Layer.InsertSublayer(preview, 0);
        SetNeedsLayout();
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        if (_preview is not null) _preview.Frame = Bounds;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _preview?.RemoveFromSuperLayer();
            _preview?.Dispose();
            _preview = null;
        }
        base.Dispose(disposing);
    }
}
