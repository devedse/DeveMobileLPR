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

public sealed record FrameRecognition(
    long FrameSequence,
    DateTimeOffset CapturedAt,
    IReadOnlyList<PlateObservation> Observations)
{
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public int RotationDegrees { get; init; }
}

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
    ValueTask<IReadOnlyList<PlateDetection>> DetectAsync(
        Yuv420Frame frame,
        CancellationToken cancellationToken);
}

public interface IPlateRecognizer
{
    ValueTask<PlateRead> RecognizeAsync(
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
    Task<IReadOnlyList<TripSummary>> GetTripsAsync(int limit, CancellationToken cancellationToken);
    Task<TripSummary?> GetTripAsync(long tripId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Sighting>> GetSightingsForTripAsync(long tripId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TripVehicleSummary>> GetVehiclesForTripAsync(long tripId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TripPoint>> GetTripPointsAsync(long tripId, CancellationToken cancellationToken);
    Task<IReadOnlyList<VehicleHistorySummary>> GetVehicleHistoryAsync(string? search, int limit, CancellationToken cancellationToken);
    Task<HistoryStatistics> GetStatisticsAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken);
    Task<IReadOnlyList<Sighting>> GetRecentAsync(int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<Sighting>> GetAllSightingsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Sighting>> FindByPlateAsync(string normalizedPlate, CancellationToken cancellationToken);
    Task<Sighting?> GetMostExpensiveAsync(CancellationToken cancellationToken);
    Task DeleteHistoryAsync(CancellationToken cancellationToken);
}
