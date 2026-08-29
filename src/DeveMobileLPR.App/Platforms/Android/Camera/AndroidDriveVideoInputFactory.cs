using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;
using DeveMobileLPR.Geometry;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

/// <summary>Composes Android camera/network adapters without making the MAUI handler a service locator.</summary>
internal sealed class AndroidDriveVideoInputFactory
{
    public IDriveVideoInput Create(
        AndroidCameraPreviewHost host,
        Action<IReadOnlyList<PreviewSourceViewport>> previewViewportsChanged) =>
        throw new NotSupportedException(
            "The UVC branch uses its dedicated Android camera preview handler.");
}
