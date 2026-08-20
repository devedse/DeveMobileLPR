using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;

namespace DeveMobileLPR.App.Platforms.Windows.Camera;

/// <summary>Composes Windows webcam/network adapters outside the MAUI handler.</summary>
internal sealed class WindowsDriveVideoInputFactory(
    AppSettings settings,
    DriveCoordinator coordinator)
{
    public WindowsDriveVideoInput Create(WindowsCameraPreviewHost host) =>
        new WindowsDriveVideoInput(
            host.WebcamPreview,
            host.NetworkPreview,
            settings.NetworkStreamUrl,
            () => settings.RecognitionFramesPerSecond,
            () => coordinator.HasPendingRecognitionFrame,
            coordinator.SubmitFrame);
}
