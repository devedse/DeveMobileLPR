using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;
using DeveMobileLPR.Geometry;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

/// <summary>Composes Android camera/network adapters without making the MAUI handler a service locator.</summary>
internal sealed class AndroidDriveVideoInputFactory(
    AndroidCameraLifecycleOwner lifecycleOwner,
    IDriveSourceCatalog sourceCatalog,
    AppSettings settings,
    DriveCoordinator coordinator)
{
    public IDriveVideoInput Create(
        AndroidCameraPreviewHost host,
        Action<IReadOnlyList<PreviewSourceViewport>> previewViewportsChanged) =>
        new AndroidDriveVideoInput(
            host.Context ?? throw new InvalidOperationException("The Android preview host has no Context."),
            lifecycleOwner,
            sourceCatalog,
            host.PreviewGrid,
            host.NetworkPreview,
            settings.NetworkStreamUrl,
            () => settings.RecognitionFramesPerSecond,
            coordinator.HasPendingRecognitionFrameFor,
            coordinator.SubmitFrame,
            previewViewportsChanged);
}
