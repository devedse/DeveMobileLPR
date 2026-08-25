using DeveMobileLPR.App.Controls;
using DeveMobileLPR.Application;
using DeveMobileLPR.Geometry;
using Microsoft.Extensions.DependencyInjection;

namespace DeveMobileLPR.App.Handlers;

internal partial class CameraPreviewHandler
{
    private DriveVideoInputLifetime? _inputLifetime;
    private DriveVideoInputLease? _inputLease;
    private Platforms.Windows.Camera.WindowsDriveVideoInput? _platformInput;

    private partial Platforms.Windows.Camera.WindowsCameraPreviewHost CreatePlatformViewCore() => new();

    private partial void ConnectPlatformView(Platforms.Windows.Camera.WindowsCameraPreviewHost platformView)
    {
        var factory = MauiContext!.Services
            .GetRequiredService<Platforms.Windows.Camera.WindowsDriveVideoInputFactory>();
        _inputLifetime = MauiContext.Services.GetRequiredService<DriveVideoInputLifetime>();
        _platformInput = factory.Create(platformView);
        _inputLease = _inputLifetime.Attach(_platformInput);
        VirtualView.ReportInputGeneration(_inputLease.Generation);
    }

    private partial void DisconnectPlatformView(Platforms.Windows.Camera.WindowsCameraPreviewHost platformView)
    {
        _platformInput?.DeactivatePreview();
        _platformInput = null;
        if (_inputLease is not null)
        {
            VirtualView.ReportInputGeneration(0);
            _inputLifetime?.Release(_inputLease);
            _inputLease = null;
        }
        _inputLifetime = null;
    }

    private partial void UpdatePresentationMode(CameraPreview view) =>
        view.ReportPresentation(AspectScaleMode.Fit);
}
