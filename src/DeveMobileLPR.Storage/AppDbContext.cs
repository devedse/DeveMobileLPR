using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DeveMobileLPR.Storage;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TripEntity> Trips => Set<TripEntity>();
    public DbSet<SightingEntity> Sightings => Set<SightingEntity>();
    public DbSet<TripPointEntity> TripPoints => Set<TripPointEntity>();

    // UTC "O" round-trip timestamps stay lexicographically sortable, matching the
    // previous storage format exactly.
    private static readonly ValueConverter<DateTimeOffset, string> UtcTimestamp = new(
        value => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var trip = modelBuilder.Entity<TripEntity>();
        trip.ToTable("trips");
        trip.Property(t => t.StartedAt).HasColumnName("started_at").HasConversion(UtcTimestamp);
        trip.Property(t => t.EndedAt).HasColumnName("ended_at").HasConversion(UtcTimestamp);
        trip.Property(t => t.DistanceMeters).HasColumnName("distance_meters");
        trip.Property(t => t.StartLatitude).HasColumnName("start_latitude");
        trip.Property(t => t.StartLongitude).HasColumnName("start_longitude");
        trip.Property(t => t.StartAccuracyMeters).HasColumnName("start_accuracy_meters");
        trip.Property(t => t.EndLatitude).HasColumnName("end_latitude");
        trip.Property(t => t.EndLongitude).HasColumnName("end_longitude");
        trip.Property(t => t.EndAccuracyMeters).HasColumnName("end_accuracy_meters");
        trip.HasIndex(t => t.StartedAt)
            .IsDescending()
            .HasDatabaseName("ix_trips_started");
        trip.HasMany(t => t.Points).WithOne()
            .HasForeignKey(point => point.TripId)
            .OnDelete(DeleteBehavior.Cascade);
        trip.HasMany(t => t.Sightings).WithOne()
            .HasForeignKey(sighting => sighting.TripId)
            .OnDelete(DeleteBehavior.SetNull);

        var sighting = modelBuilder.Entity<SightingEntity>();
        sighting.ToTable("sightings");
        sighting.Property(s => s.NormalizedPlate).HasColumnName("normalized_plate");
        sighting.Property(s => s.DisplayPlate).HasColumnName("display_plate");
        sighting.Property(s => s.Region).HasColumnName("region");
        sighting.Property(s => s.FirstSeenAt).HasColumnName("first_seen_at").HasConversion(UtcTimestamp);
        sighting.Property(s => s.LastSeenAt).HasColumnName("last_seen_at").HasConversion(UtcTimestamp);
        sighting.Property(s => s.Confidence).HasColumnName("confidence");
        sighting.Property(s => s.ObservationCount).HasColumnName("observation_count");
        sighting.Property(s => s.Latitude).HasColumnName("latitude");
        sighting.Property(s => s.Longitude).HasColumnName("longitude");
        sighting.Property(s => s.LocationAccuracyMeters).HasColumnName("location_accuracy_meters");
        sighting.Property(s => s.Make).HasColumnName("make");
        sighting.Property(s => s.Model).HasColumnName("model");
        sighting.Property(s => s.CatalogPrice).HasColumnName("catalog_price").HasColumnType("NUMERIC");
        sighting.Property(s => s.RegistrationYear).HasColumnName("registration_year");
        sighting.Property(s => s.FuelDescription).HasColumnName("fuel_description");
        sighting.Property(s => s.BodyType).HasColumnName("body_type");
        sighting.Property(s => s.TripId).HasColumnName("trip_id");
        sighting.Property(s => s.SnapshotReference).HasColumnName("snapshot_reference");
        sighting.HasIndex(s => new { s.NormalizedPlate, s.LastSeenAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_sightings_plate_last_seen");
        sighting.HasIndex(s => s.CatalogPrice)
            .IsDescending()
            .HasFilter("\"catalog_price\" IS NOT NULL")
            .HasDatabaseName("ix_sightings_price");
        sighting.HasIndex(s => new { s.TripId, s.LastSeenAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_sightings_trip_last_seen");
        sighting.HasIndex(s => s.LastSeenAt)
            .IsDescending()
            .HasDatabaseName("ix_sightings_last_seen");

        var tripPoint = modelBuilder.Entity<TripPointEntity>();
        tripPoint.ToTable("trip_points");
        tripPoint.Property(p => p.TripId).HasColumnName("trip_id");
        tripPoint.Property(p => p.RecordedAt).HasColumnName("recorded_at").HasConversion(UtcTimestamp);
        tripPoint.Property(p => p.Latitude).HasColumnName("latitude");
        tripPoint.Property(p => p.Longitude).HasColumnName("longitude");
        tripPoint.Property(p => p.AccuracyMeters).HasColumnName("accuracy_meters");
        tripPoint.HasIndex(p => new { p.TripId, p.RecordedAt })
            .HasDatabaseName("ix_trip_points_trip_time");
    }
}

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

    public List<TripPointEntity> Points { get; set; } = [];
    public List<SightingEntity> Sightings { get; set; } = [];
}

public sealed class SightingEntity
{
    public long Id { get; set; }
    public required string NormalizedPlate { get; set; }
    public required string DisplayPlate { get; set; }
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
    public long? TripId { get; set; }
    public string? SnapshotReference { get; set; }
}

public sealed class TripPointEntity
{
    public long Id { get; set; }
    public long TripId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public float? AccuracyMeters { get; set; }
}
