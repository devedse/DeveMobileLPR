namespace DeveMobileLPR.Storage.Models;

/// <summary>A filtered GPS sample on a trip's route.</summary>
public sealed class TripPointModel
{
    public long Id { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public float? AccuracyMeters { get; set; }

    public long TripId { get; set; }
    public TripModel Trip { get; set; } = null!;
}
