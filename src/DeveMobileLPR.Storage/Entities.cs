namespace DeveMobileLPR.Storage;

/// <summary>A single drive, from the moment recognition starts until it stops.</summary>
public sealed class TripEntity
{
    public long Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public double DistanceMeters { get; set; }
    public double? StartLatitude { get; set; }
    public double? StartLongitude { get; set; }
    public float? StartAccuracyMeters { get; set; }
    public double? EndLatitude { get; set; }
    public double? EndLongitude { get; set; }
    public float? EndAccuracyMeters { get; set; }

    public ICollection<SightingEntity> Sightings { get; } = [];
    public ICollection<TripPointEntity> Points { get; } = [];
}

/// <summary>One confirmed appearance of a plate, enriched with the RDW facts known at the time.</summary>
public sealed class SightingEntity
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
    public TripEntity? Trip { get; set; }
}

/// <summary>A filtered GPS sample on a trip's route.</summary>
public sealed class TripPointEntity
{
    public long Id { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public float? AccuracyMeters { get; set; }

    public long TripId { get; set; }
    public TripEntity Trip { get; set; } = null!;
}
