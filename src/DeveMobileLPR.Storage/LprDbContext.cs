using System.Globalization;
using DeveMobileLPR.Storage.Models;
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

    public DbSet<TripModel> Trips => Set<TripModel>();
    public DbSet<SightingModel> Sightings => Set<SightingModel>();
    public DbSet<TripPointModel> TripPoints => Set<TripPointModel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<TripModel>(trip =>
        {
            trip.ToTable("trips");
            trip.HasKey(model => model.Id);
            trip.Property(model => model.Id).HasColumnName("id");
            trip.Property(model => model.StartedAt).HasColumnName("started_at").HasConversion(TimestampConverter);
            trip.Property(model => model.EndedAt).HasColumnName("ended_at").HasConversion(TimestampConverter);
            trip.Property(model => model.DistanceMeters).HasColumnName("distance_meters");
            trip.Property(model => model.StartLatitude).HasColumnName("start_latitude");
            trip.Property(model => model.StartLongitude).HasColumnName("start_longitude");
            trip.Property(model => model.StartAccuracyMeters).HasColumnName("start_accuracy_meters");
            trip.Property(model => model.EndLatitude).HasColumnName("end_latitude");
            trip.Property(model => model.EndLongitude).HasColumnName("end_longitude");
            trip.Property(model => model.EndAccuracyMeters).HasColumnName("end_accuracy_meters");
            trip.HasIndex(model => model.StartedAt)
                .HasDatabaseName("ix_trips_started")
                .IsDescending(true);
        });

        modelBuilder.Entity<SightingModel>(sighting =>
        {
            sighting.ToTable("sightings");
            sighting.HasKey(model => model.Id);
            sighting.Property(model => model.Id).HasColumnName("id");
            sighting.Property(model => model.NormalizedPlate).HasColumnName("normalized_plate");
            sighting.Property(model => model.DisplayPlate).HasColumnName("display_plate");
            sighting.Property(model => model.Region).HasColumnName("region");
            sighting.Property(model => model.FirstSeenAt).HasColumnName("first_seen_at").HasConversion(TimestampConverter);
            sighting.Property(model => model.LastSeenAt).HasColumnName("last_seen_at").HasConversion(TimestampConverter);
            sighting.Property(model => model.Confidence).HasColumnName("confidence");
            sighting.Property(model => model.ObservationCount).HasColumnName("observation_count");
            sighting.Property(model => model.Latitude).HasColumnName("latitude");
            sighting.Property(model => model.Longitude).HasColumnName("longitude");
            sighting.Property(model => model.LocationAccuracyMeters).HasColumnName("location_accuracy_meters");
            sighting.Property(model => model.Make).HasColumnName("make");
            sighting.Property(model => model.Model).HasColumnName("model");
            // SQLite has no decimal type, and EF Core refuses to sort or compare a decimal stored as
            // text. Catalog prices are whole euros, so REAL is both exact enough and orderable.
            sighting.Property(model => model.CatalogPrice).HasColumnName("catalog_price").HasConversion<double>();
            sighting.Property(model => model.RegistrationYear).HasColumnName("registration_year");
            sighting.Property(model => model.FuelDescription).HasColumnName("fuel_description");
            sighting.Property(model => model.BodyType).HasColumnName("body_type");
            sighting.Property(model => model.SnapshotReference).HasColumnName("snapshot_reference");
            sighting.Property(model => model.TripId).HasColumnName("trip_id");
            sighting.HasOne(model => model.Trip)
                .WithMany(model => model.Sightings)
                .HasForeignKey(model => model.TripId)
                .OnDelete(DeleteBehavior.SetNull);
            sighting.HasIndex(model => new { model.NormalizedPlate, model.LastSeenAt })
                .HasDatabaseName("ix_sightings_plate_last_seen")
                .IsDescending(false, true);
            sighting.HasIndex(model => model.CatalogPrice)
                .HasDatabaseName("ix_sightings_price")
                .IsDescending(true)
                .HasFilter("\"catalog_price\" IS NOT NULL");
            sighting.HasIndex(model => new { model.TripId, model.LastSeenAt })
                .HasDatabaseName("ix_sightings_trip_last_seen")
                .IsDescending(false, true);
        });

        modelBuilder.Entity<TripPointModel>(point =>
        {
            point.ToTable("trip_points");
            point.HasKey(model => model.Id);
            point.Property(model => model.Id).HasColumnName("id");
            point.Property(model => model.RecordedAt).HasColumnName("recorded_at").HasConversion(TimestampConverter);
            point.Property(model => model.Latitude).HasColumnName("latitude");
            point.Property(model => model.Longitude).HasColumnName("longitude");
            point.Property(model => model.AccuracyMeters).HasColumnName("accuracy_meters");
            point.Property(model => model.TripId).HasColumnName("trip_id");
            point.HasOne(model => model.Trip)
                .WithMany(model => model.Points)
                .HasForeignKey(model => model.TripId)
                .OnDelete(DeleteBehavior.Cascade);
            point.HasIndex(model => new { model.TripId, model.RecordedAt })
                .HasDatabaseName("ix_trip_points_trip_time");
        });
    }
}
