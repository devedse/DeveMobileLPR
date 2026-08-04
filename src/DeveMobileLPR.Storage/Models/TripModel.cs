namespace DeveMobileLPR.Storage.Models;

/// <summary>A single drive, from the moment recognition starts until it stops.</summary>
public sealed class TripModel
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

    public ICollection<SightingModel> Sightings { get; } = [];
    public ICollection<TripPointModel> Points { get; } = [];
}
