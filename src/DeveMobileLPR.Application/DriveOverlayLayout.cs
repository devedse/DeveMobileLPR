using DeveMobileLPR.Geometry;

namespace DeveMobileLPR.Application;

public static class DriveOverlayLayout
{
    public static IReadOnlyList<DriveOverlay> GetVisibleOverlays(DriveSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsDriving)
        {
            return [];
        }

        return snapshot.Overlays
            .Where(overlay => snapshot.TrackingDiagnosticsEnabled
                || overlay.Kind is not (DriveOverlayKind.Candidate or DriveOverlayKind.Track))
            .OrderBy(static overlay => overlay.Kind)
            .ToArray();
    }

    public static bool TryProject(
        DriveOverlay overlay,
        float viewportWidth,
        float viewportHeight,
        AspectScaleMode scaleMode,
        out BoundingBox projected)
    {
        if (overlay.SourceWidth <= 1
            || overlay.SourceHeight <= 1
            || !float.IsFinite(viewportWidth)
            || !float.IsFinite(viewportHeight)
            || viewportWidth <= 0
            || viewportHeight <= 0)
        {
            projected = default;
            return false;
        }

        projected = AspectRatioTransform.Create(
            overlay.SourceWidth,
            overlay.SourceHeight,
            viewportWidth,
            viewportHeight,
            scaleMode).Project(overlay.Bounds);
        return !projected.IsEmpty;
    }
}
