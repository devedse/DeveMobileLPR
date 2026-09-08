using DeveMobileLPR.App.Controls;
using DeveMobileLPR.Application;
using DeveMobileLPR.Geometry;
using Microsoft.Extensions.DependencyInjection;

namespace DeveMobileLPR.App.Handlers;

internal partial class CameraPreviewHandler
{
    private DriveVideoInputLifetime? _inputLifetime;
    private DriveVideoInputLease? _inputLease;

    private partial IosCameraPreviewHost CreatePlatformViewCore() => new();

    private partial void ConnectPlatformView(IosCameraPreviewHost platformView)
    {
        var factory = MauiContext!.Services.GetRequiredService<IosDriveVideoInputFactory>();
        _inputLifetime = MauiContext.Services.GetRequiredService<DriveVideoInputLifetime>();
        _inputLease = _inputLifetime.Attach(factory.Create(platformView));
        VirtualView.ReportInputGeneration(_inputLease.Generation);
    }

    private partial void DisconnectPlatformView(IosCameraPreviewHost platformView)
    {
        if (_inputLease is not null)
        {
            VirtualView.ReportInputGeneration(0);
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
