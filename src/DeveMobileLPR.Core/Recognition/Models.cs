using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Recognition;

public sealed record PlateDetection(BoundingBox Bounds, float Confidence);

public sealed record CharacterCandidate(char Character, float Probability);

public sealed record CharacterHypothesis(IReadOnlyList<CharacterCandidate> Candidates)
{
    public CharacterCandidate Best => Candidates.Count == 0
        ? new CharacterCandidate('_', 0)
        : Candidates[0];
}

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
    TimeSpan ProcessingTime,
    IReadOnlyList<PlateObservation> Observations);

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
    VehicleRecord? Vehicle);

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
    Task<Sighting> AddOrMergeAsync(ConfirmedPlate plate, GeoPoint? location, VehicleRecord? vehicle, CancellationToken cancellationToken);
    Task<IReadOnlyList<Sighting>> GetRecentAsync(int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<Sighting>> FindByPlateAsync(string normalizedPlate, CancellationToken cancellationToken);
    Task<Sighting?> GetMostExpensiveAsync(CancellationToken cancellationToken);
}
