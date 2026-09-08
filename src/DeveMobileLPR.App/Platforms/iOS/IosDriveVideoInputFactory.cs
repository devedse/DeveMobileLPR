using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;

namespace DeveMobileLPR.App;

/// <summary>Composes the iPhone AVFoundation adapter outside the MAUI handler.</summary>
internal sealed class IosDriveVideoInputFactory(
    IDriveSourceCatalog sourceCatalog,
    AppSettings settings,
    DriveCoordinator coordinator)
{
    public IDriveVideoInput Create(IosCameraPreviewHost host) =>
        new IosDriveFrameSource(
            host,
            sourceCatalog,
            () => settings.RecognitionFramesPerSecond,
            () => coordinator.HasPendingRecognitionFrame,
            frame => coordinator.SubmitFrame(frame),
            settings.NetworkStreamUrl);
}
