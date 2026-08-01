using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Recognition;

public sealed record PlateDetection(BoundingBox Bounds, float Confidence);

public sealed record CharacterCandidate(char Character, float Probability);

public sealed record CharacterHypothesis(IReadOnlyList<CharacterCandidate> Candidates);

public sealed record PlateRead(
    string Text,
    float Confidence,
    IReadOnlyList<CharacterHypothesis> Characters,
    string? Region,
    float? RegionConfidence);

public sealed record PlateObservation(
    long FrameSequence,
    DateTimeOffset CapturedAt,
    PlateDetection Detection,
    PlateRead Read,
    float Quality);

public sealed record ModelExecutionTiming(
    double QueueMilliseconds,
    double PreprocessingMilliseconds,
    double InferenceMilliseconds,
    double PostprocessingMilliseconds)
{
    public static ModelExecutionTiming Empty { get; } = new(0, 0, 0, 0);

    public double TotalMilliseconds =>
        QueueMilliseconds + PreprocessingMilliseconds + InferenceMilliseconds + PostprocessingMilliseconds;

    public static ModelExecutionTiming operator +(ModelExecutionTiming left, ModelExecutionTiming right) => new(
        left.QueueMilliseconds + right.QueueMilliseconds,
        left.PreprocessingMilliseconds + right.PreprocessingMilliseconds,
        left.InferenceMilliseconds + right.InferenceMilliseconds,
        left.PostprocessingMilliseconds + right.PostprocessingMilliseconds);
}

public sealed record PlateDetectionResult(
    IReadOnlyList<PlateDetection> Detections,
    ModelExecutionTiming Timing);

public sealed record PlateRecognitionResult(
    PlateRead Read,
    ModelExecutionTiming Timing);

public sealed record PlateCandidateDiagnostics(
    PlateDetection Detection,
    float? Quality,
    bool OcrAttempted,
    string? ReadText,
    float? OcrConfidence,
    ModelExecutionTiming? OcrTiming);

public sealed record RecognitionFrameDiagnostics(
    double TotalMilliseconds,
    ModelExecutionTiming Detector,
    ModelExecutionTiming Ocr,
    int DetectionCount,
    int OcrAttemptCount,
    int ObservationCount)
{
    public static RecognitionFrameDiagnostics Empty { get; } = new(
        0,
        ModelExecutionTiming.Empty,
        ModelExecutionTiming.Empty,
        0,
        0,
        0);

    public IReadOnlyList<PlateCandidateDiagnostics> Candidates { get; init; } = [];
    public double CropQualityMilliseconds { get; init; }
    public string DetectorBackend { get; init; } = "Unknown";
    public string OcrBackend { get; init; } = "Unknown";
}

public sealed record FrameRecognition(
    long FrameSequence,
    DateTimeOffset CapturedAt,
    IReadOnlyList<PlateObservation> Observations)
{
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public int RotationDegrees { get; init; }
    public RecognitionFrameDiagnostics Diagnostics { get; init; } = RecognitionFrameDiagnostics.Empty;
}

public sealed record PlateTrackSnapshot(
    Guid TrackId,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    BoundingBox Bounds,
    int ObservationCount,
    bool Confirmed,
    long LastFrameSequence,
    string LastRead,
    float DetectorConfidence,
    float OcrConfidence,
    float Quality);

public enum PlateAssociationKind
{
    Unspecified,
    NewTrack,
    ExactText,
    SimilarText,
    PredictedMotion
}

public sealed record PlateTrackAssociation(
    Guid TrackId,
    long FrameSequence,
    bool Created,
    float? IntersectionOverUnion)
{
    public PlateAssociationKind Kind { get; init; }
    public BoundingBox? PredictedBounds { get; init; }
    public float? PredictedIntersectionOverUnion { get; init; }
    public float? FrameCenterDistance { get; init; }
    public float? ScaleRatio { get; init; }
    public int? TextEditDistance { get; init; }
    public float? Score { get; init; }
}

public sealed record PlateTrackingUpdate(
    IReadOnlyList<ConfirmedPlate> Confirmations,
    IReadOnlyList<PlateTrackSnapshot> Tracks,
    IReadOnlyList<PlateTrackAssociation> Associations);

public sealed record RecognitionStreamDiagnostics(
    RecognitionFrameDiagnostics Frame,
    double TrackingMilliseconds,
    IReadOnlyList<PlateTrackSnapshot> Tracks,
    IReadOnlyList<PlateTrackAssociation> Associations)
{
    public double TotalMilliseconds => Frame.TotalMilliseconds + TrackingMilliseconds;
    public long ReplacedInputFrames { get; init; }
}

public sealed record RecognitionStreamResult(
    FrameRecognition Recognition,
    IReadOnlyList<ConfirmedPlate> Confirmations,
    RecognitionStreamDiagnostics Diagnostics);

public sealed record ConsensusResult(
    string NormalizedPlate,
    string DisplayPlate,
    string? Region,
    float Confidence,
    int ObservationCount);

public sealed record ConfirmedPlate(
    Guid TrackId,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    BoundingBox LastBounds,
    ConsensusResult Consensus);

public readonly record struct GeoPoint(double Latitude, double Longitude, float? AccuracyMeters);

public sealed record VehicleRecord(
    string NormalizedPlate,
    string? Make,
    string? Model,
    decimal? CatalogPrice,
    int? RegistrationYear,
    string? FuelDescription,
    string? BodyType);

public sealed record Sighting(
    long Id,
    string NormalizedPlate,
    string DisplayPlate,
    string? Region,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    float Confidence,
    int ObservationCount,
    GeoPoint? Location,
    VehicleRecord? Vehicle)
{
    public long? TripId { get; init; }
}

public sealed record TripSummary(
    long Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    double DistanceMeters,
    int SightingCount,
    int UniqueVehicleCount,
    decimal? MostExpensiveCatalogPrice,
    string? MostExpensiveDisplayPlate,
    GeoPoint? StartLocation,
    GeoPoint? EndLocation)
{
    public TimeSpan Duration => (EndedAt ?? DateTimeOffset.UtcNow) - StartedAt;
}

public sealed record TripPoint(
    long Id,
    long TripId,
    DateTimeOffset RecordedAt,
    GeoPoint Location);

public sealed record VehicleHistorySummary(
    string NormalizedPlate,
    string DisplayPlate,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int SightingCount,
    int TripCount,
    VehicleRecord? Vehicle,
    GeoPoint? LastLocation);

public enum VehicleHistorySort
{
    MostRecent,
    HighestValue
}

public sealed record VehicleHistoryQuery(
    string? Search,
    DateTimeOffset? SeenSince,
    decimal? MinimumCatalogPrice,
    VehicleHistorySort Sort,
    int Offset,
    int Limit);

public sealed record TripVehicleSummary(
    string NormalizedPlate,
    string DisplayPlate,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    float Confidence,
    int ObservationCount,
    int SightingCount,
    int EarlierSightingCount,
    VehicleRecord? Vehicle,
    GeoPoint? LastLocation);

public sealed record HistoryStatistics(
    int TripCount,
    int SightingCount,
    int UniqueVehicleCount,
    double DistanceMeters,
    Sighting? MostExpensiveSighting);

public interface IPlateDetector
{
    ValueTask<PlateDetectionResult> DetectAsync(
        Yuv420Frame frame,
        CancellationToken cancellationToken);
}

public interface IInferenceBackendInfo
{
    string BackendName { get; }
}

public interface IPlateRecognizer
{
    ValueTask<PlateRecognitionResult> RecognizeAsync(
        Yuv420Frame frame,
        BoundingBox plateBounds,
        CancellationToken cancellationToken);
}

public interface IFrameRecognitionPipeline
{
    ValueTask<FrameRecognition> ProcessAsync(Yuv420Frame frame, CancellationToken cancellationToken);
}

public interface IVehicleLookup
{
    ValueTask<VehicleRecord?> FindAsync(string normalizedPlate, CancellationToken cancellationToken);
}

public interface ISightingRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<Sighting> AddOrMergeAsync(ConfirmedPlate plate, GeoPoint? location, VehicleRecord? vehicle, long? tripId, CancellationToken cancellationToken);
    Task<TripSummary> StartTripAsync(DateTimeOffset startedAt, GeoPoint? location, CancellationToken cancellationToken);
    Task<TripSummary> EndTripAsync(long tripId, DateTimeOffset endedAt, GeoPoint? location, CancellationToken cancellationToken);
    Task AddTripPointAsync(long tripId, DateTimeOffset recordedAt, GeoPoint location, CancellationToken cancellationToken);
    Task<IReadOnlyList<TripSummary>> GetTripsAsync(int offset, int limit, CancellationToken cancellationToken);
    Task<TripSummary?> GetTripAsync(long tripId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Sighting>> GetSightingsForTripAsync(long tripId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TripVehicleSummary>> GetVehiclesForTripAsync(long tripId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TripPoint>> GetTripPointsAsync(long tripId, CancellationToken cancellationToken);
    Task<IReadOnlyList<VehicleHistorySummary>> GetVehicleHistoryAsync(VehicleHistoryQuery query, CancellationToken cancellationToken);
    Task<HistoryStatistics> GetStatisticsAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken);
    Task<IReadOnlyList<Sighting>> GetRecentAsync(int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<Sighting>> GetAllSightingsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Sighting>> FindByPlateAsync(string normalizedPlate, CancellationToken cancellationToken);
    Task<Sighting?> GetMostExpensiveAsync(CancellationToken cancellationToken);
    Task DeleteHistoryAsync(CancellationToken cancellationToken);
}
