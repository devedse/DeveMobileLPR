using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DeveMobileLPR.Storage;

/// <summary>Code-first model for the app's own recognition history database.</summary>
public sealed class LprDbContext(DbContextOptions<LprDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Timestamps are stored as round-trip UTC text. The format is fixed width, so SQLite's binary
    /// collation makes range filters, ORDER BY and MIN/MAX chronological without extra conversions.
    /// </summary>
    private static readonly ValueConverter<DateTimeOffset, string> TimestampConverter = new(
        value => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    public DbSet<TripEntity> Trips => Set<TripEntity>();
    public DbSet<SightingEntity> Sightings => Set<SightingEntity>();
    public DbSet<TripPointEntity> TripPoints => Set<TripPointEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<TripEntity>(trip =>
        {
            trip.ToTable("trips");
            trip.HasKey(entity => entity.Id);
            trip.Property(entity => entity.Id).HasColumnName("id");
            trip.Property(entity => entity.StartedAt).HasColumnName("started_at").HasConversion(TimestampConverter);
            trip.Property(entity => entity.EndedAt).HasColumnName("ended_at").HasConversion(TimestampConverter);
            trip.Property(entity => entity.DistanceMeters).HasColumnName("distance_meters");
            trip.Property(entity => entity.StartLatitude).HasColumnName("start_latitude");
            trip.Property(entity => entity.StartLongitude).HasColumnName("start_longitude");
            trip.Property(entity => entity.StartAccuracyMeters).HasColumnName("start_accuracy_meters");
            trip.Property(entity => entity.EndLatitude).HasColumnName("end_latitude");
            trip.Property(entity => entity.EndLongitude).HasColumnName("end_longitude");
            trip.Property(entity => entity.EndAccuracyMeters).HasColumnName("end_accuracy_meters");
            trip.HasIndex(entity => entity.StartedAt)
                .HasDatabaseName("ix_trips_started")
                .IsDescending(true);
        });

        modelBuilder.Entity<SightingEntity>(sighting =>
        {
            sighting.ToTable("sightings");
            sighting.HasKey(entity => entity.Id);
            sighting.Property(entity => entity.Id).HasColumnName("id");
            sighting.Property(entity => entity.NormalizedPlate).HasColumnName("normalized_plate");
            sighting.Property(entity => entity.DisplayPlate).HasColumnName("display_plate");
            sighting.Property(entity => entity.Region).HasColumnName("region");
            sighting.Property(entity => entity.FirstSeenAt).HasColumnName("first_seen_at").HasConversion(TimestampConverter);
            sighting.Property(entity => entity.LastSeenAt).HasColumnName("last_seen_at").HasConversion(TimestampConverter);
            sighting.Property(entity => entity.Confidence).HasColumnName("confidence");
            sighting.Property(entity => entity.ObservationCount).HasColumnName("observation_count");
            sighting.Property(entity => entity.Latitude).HasColumnName("latitude");
            sighting.Property(entity => entity.Longitude).HasColumnName("longitude");
            sighting.Property(entity => entity.LocationAccuracyMeters).HasColumnName("location_accuracy_meters");
            sighting.Property(entity => entity.Make).HasColumnName("make");
            sighting.Property(entity => entity.Model).HasColumnName("model");
            // SQLite has no decimal type, and EF Core refuses to sort or compare a decimal stored as
            // text. Catalog prices are whole euros, so REAL is both exact enough and orderable.
            sighting.Property(entity => entity.CatalogPrice).HasColumnName("catalog_price").HasConversion<double>();
            sighting.Property(entity => entity.RegistrationYear).HasColumnName("registration_year");
            sighting.Property(entity => entity.FuelDescription).HasColumnName("fuel_description");
            sighting.Property(entity => entity.BodyType).HasColumnName("body_type");
            sighting.Property(entity => entity.SnapshotReference).HasColumnName("snapshot_reference");
            sighting.Property(entity => entity.TripId).HasColumnName("trip_id");
            sighting.HasOne(entity => entity.Trip)
                .WithMany(entity => entity.Sightings)
                .HasForeignKey(entity => entity.TripId)
                .OnDelete(DeleteBehavior.SetNull);
            sighting.HasIndex(entity => new { entity.NormalizedPlate, entity.LastSeenAt })
                .HasDatabaseName("ix_sightings_plate_last_seen")
                .IsDescending(false, true);
            sighting.HasIndex(entity => entity.CatalogPrice)
                .HasDatabaseName("ix_sightings_price")
                .IsDescending(true)
                .HasFilter("\"catalog_price\" IS NOT NULL");
            sighting.HasIndex(entity => new { entity.TripId, entity.LastSeenAt })
                .HasDatabaseName("ix_sightings_trip_last_seen")
                .IsDescending(false, true);
        });

        modelBuilder.Entity<TripPointEntity>(point =>
        {
            point.ToTable("trip_points");
            point.HasKey(entity => entity.Id);
            point.Property(entity => entity.Id).HasColumnName("id");
            point.Property(entity => entity.RecordedAt).HasColumnName("recorded_at").HasConversion(TimestampConverter);
            point.Property(entity => entity.Latitude).HasColumnName("latitude");
            point.Property(entity => entity.Longitude).HasColumnName("longitude");
            point.Property(entity => entity.AccuracyMeters).HasColumnName("accuracy_meters");
            point.Property(entity => entity.TripId).HasColumnName("trip_id");
            point.HasOne(entity => entity.Trip)
                .WithMany(entity => entity.Points)
                .HasForeignKey(entity => entity.TripId)
                .OnDelete(DeleteBehavior.Cascade);
            point.HasIndex(entity => new { entity.TripId, entity.RecordedAt })
                .HasDatabaseName("ix_trip_points_trip_time");
        });
    }
}
