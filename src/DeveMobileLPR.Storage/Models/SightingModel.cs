namespace DeveMobileLPR.Storage.Models;

/// <summary>One confirmed appearance of a plate, enriched with the RDW facts known at the time.</summary>
public sealed class SightingModel
{
    public long Id { get; set; }
    public string NormalizedPlate { get; set; } = null!;
    public string DisplayPlate { get; set; } = null!;
    public string? Region { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public float Confidence { get; set; }
    public int ObservationCount { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public float? LocationAccuracyMeters { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public decimal? CatalogPrice { get; set; }
    public int? RegistrationYear { get; set; }
    public string? FuelDescription { get; set; }
    public string? BodyType { get; set; }
    public string? SnapshotReference { get; set; }

    public long? TripId { get; set; }
    public TripModel? Trip { get; set; }
}
