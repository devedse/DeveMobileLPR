using DeveMobileLPR.App.Controls;
using DeveMobileLPR.Application;
using DeveMobileLPR.Geometry;
using Microsoft.Extensions.DependencyInjection;

namespace DeveMobileLPR.App.Handlers;

internal partial class CameraPreviewHandler
{
    private DriveVideoInputLifetime? _inputLifetime;
    private DriveVideoInputLease? _inputLease;

    private partial Platforms.Android.Camera.AndroidCameraPreviewHost CreatePlatformViewCore()
    {
        var context = MauiContext?.Context
            ?? throw new InvalidOperationException("Android context is unavailable.");
        return new Platforms.Android.Camera.AndroidCameraPreviewHost(context);
    }

    private partial void ConnectPlatformView(Platforms.Android.Camera.AndroidCameraPreviewHost platformView)
    {
        var factory = MauiContext!.Services
            .GetRequiredService<Platforms.Android.Camera.AndroidDriveVideoInputFactory>();
        _inputLifetime = MauiContext.Services.GetRequiredService<DriveVideoInputLifetime>();
        var virtualView = VirtualView;
        var input = factory.Create(
            platformView,
            viewports => MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_inputLease is not null && ReferenceEquals(VirtualView, virtualView))
                {
                    virtualView.ReportSourceViewports(viewports);
                }
            }));
        _inputLease = _inputLifetime.Attach(input);
    }

    private partial void DisconnectPlatformView(Platforms.Android.Camera.AndroidCameraPreviewHost platformView)
    {
        if (_inputLease is not null)
        {
            _inputLifetime?.Release(_inputLease);
            _inputLease = null;
        }
        _inputLifetime = null;
    }

    private partial void UpdatePresentationMode(CameraPreview view) =>
        view.ReportPresentation(
            view.IsNetworkStream || view.IsMultiSource
                ? AspectScaleMode.Fit
                : AspectScaleMode.Fill);
}
