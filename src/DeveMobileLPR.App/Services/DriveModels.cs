using DeveMobileLPR.Geometry;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.Services;

internal sealed record DriveOverlay(
    BoundingBox Bounds,
    int SourceWidth,
    int SourceHeight,
    string Title,
    string Detail,
    float Confidence,
    bool Confirmed);

internal sealed record DriveSnapshot(
    bool IsInitializing,
    bool IsReady,
    bool IsDriving,
    bool IsStopping,
    string Status,
    bool HasError,
    DateTimeOffset? StartedAt,
    int UniqueVehicles,
    IReadOnlyList<Sighting> RecentSightings,
    Sighting? MostExpensive,
    IReadOnlyList<DriveOverlay> Overlays,
    bool HasLocation,
    bool IsInputReady,
    bool SupportsNetworkStreams,
    IReadOnlyList<CameraChoice> CameraChoices,
    string SelectedCameraId);