using DeveMobileLPR.Geometry;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application;

public sealed record CameraChoice(string Id, string Name);

public static class DriveInputIds
{
    public const string RearCamera = "rear";
    public const string FrontCamera = "front";
    public const string ExternalCamera = "external";
    public const string NetworkLlHls = "network:llhls";
}

public sealed record DriveOverlay(
    BoundingBox Bounds,
    int SourceWidth,
    int SourceHeight,
    string Title,
    string Detail,
    float Confidence,
    DriveOverlayKind Kind);

/// <summary>
/// Overlay classes in draw order: later members are drawn on top of earlier ones.
/// </summary>
public enum DriveOverlayKind
{
    /// <summary>Detector output before consensus, shown only with tracking diagnostics on.</summary>
    Candidate,

    /// <summary>Where a track was predicted to be this frame, shown only with diagnostics on.</summary>
    Prediction,

    Reading,
    Track,

    /// <summary>Confirmed, and not seen on any earlier trip.</summary>
    Confirmed,

    /// <summary>Confirmed, and seen at least once on an earlier trip.</summary>
    ConfirmedKnown
}

public sealed record DriveIntervalDiagnostics(string Label, double? IntervalMilliseconds);

public sealed record DriveDiagnosticsSnapshot(
    DriveIntervalDiagnostics Source,
    DriveIntervalDiagnostics Preview,
    RecognitionStreamDiagnostics? Recognition)
{
    public static DriveDiagnosticsSnapshot Empty { get; } = new(
        new("Capture interval", null),
        new("Preview interval", null),
        null);

    public DriveDiagnosticsSnapshot WithSourceLabel(string label) =>
        this with { Source = Source with { Label = label } };
}

public sealed record DriveSnapshot(
    bool IsInitializing,
    bool IsReady,
    bool IsDriving,
    bool IsStopping,
    string Status,
    bool HasError,
    DateTimeOffset? StartedAt,
    DriveDiagnosticsSnapshot Diagnostics,
    int UniqueVehicles,
    IReadOnlyList<Sighting> RecentSightings,
    Sighting? MostExpensive,
    IReadOnlyList<DriveOverlay> Overlays,
    bool HasLocation,
    bool IsInputReady,
    bool SupportsNetworkStreams,
    IReadOnlyList<CameraChoice> CameraChoices,
    string SelectedCameraId,
    bool TrackingDiagnosticsEnabled,
    bool RecognitionStatisticsEnabled,
    bool ShowRoadGuide);
