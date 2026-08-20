using DeveMobileLPR.Geometry;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application.Tests;

public sealed class DriveOverlayLayoutTests
{
    [Fact]
    public void TryProjectUsesIdenticalFitLetterboxingForEveryRenderer()
    {
        var overlay = new DriveOverlay(
            new BoundingBox(100, 50, 300, 150),
            400,
            200,
            "plate",
            "detail",
            0.9f,
            DriveOverlayKind.Reading);

        var projected = DriveOverlayLayout.TryProject(
            overlay, 400, 400, AspectScaleMode.Fit, out var bounds);

        Assert.True(projected);
        Assert.Equal(new BoundingBox(100, 150, 300, 250), bounds);
    }

    [Fact]
    public void TryProjectMirrorsFrontPreviewWithoutChangingDetectionSize()
    {
        var overlay = new DriveOverlay(
            new BoundingBox(10, 20, 30, 40),
            100,
            100,
            "front plate",
            string.Empty,
            1,
            DriveOverlayKind.Reading);

        var projected = DriveOverlayLayout.TryProject(
            overlay, 200, 200, AspectScaleMode.Fit, true, out var bounds);

        Assert.True(projected);
        Assert.Equal(new BoundingBox(140, 40, 180, 80), bounds);
    }

    [Fact]
    public void GetVisibleOverlaysHidesDebugItemsUnlessDebugIsEnabled()
    {
        var candidate = Overlay(DriveOverlayKind.Candidate);
        var track = Overlay(DriveOverlayKind.Track);
        var reading = Overlay(DriveOverlayKind.Reading);
        var snapshot = Snapshot([candidate, track, reading], debug: false);

        Assert.Equal([reading], DriveOverlayLayout.GetVisibleOverlays(snapshot));
        Assert.Equal([reading], DriveOverlayLayout.GetVisibleOverlays(snapshot with { RecognitionStatisticsEnabled = true }));
        Assert.Equal(3, DriveOverlayLayout.GetVisibleOverlays(snapshot with { TrackingDiagnosticsEnabled = true }).Count);
        Assert.Empty(DriveOverlayLayout.GetVisibleOverlays(snapshot with { IsDriving = false }));
    }

    private static DriveOverlay Overlay(DriveOverlayKind kind) => new(
        new BoundingBox(0, 0, 10, 10), 100, 100, kind.ToString(), string.Empty, 1, kind);

    private static DriveSnapshot Snapshot(IReadOnlyList<DriveOverlay> overlays, bool debug) => new(
        false, true, true, false, "Ready", false, DateTimeOffset.UtcNow,
        DriveDiagnosticsSnapshot.Empty, 0, [], null, overlays, false, true, false, 0, true, [], "rear", debug, false, false, false);
}
