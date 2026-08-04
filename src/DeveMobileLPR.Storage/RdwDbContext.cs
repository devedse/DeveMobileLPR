using Microsoft.EntityFrameworkCore;

namespace DeveMobileLPR.Storage;

/// <summary>One row of the downloader's stable <c>rdw_vehicles</c> view.</summary>
public sealed class RdwVehicleRow
{
    public string NormalizedPlate { get; set; } = null!;
    public string? Make { get; set; }
    public string? Model { get; set; }
    public decimal? CatalogPrice { get; set; }
    public int? RegistrationYear { get; set; }
    public string? FuelDescription { get; set; }
    public string? BodyType { get; set; }
}

/// <summary>
/// Read-only model over the separate RDW database. It is deliberately not Code First: the file is
/// produced by the downloader and only ever consumed here, through a view the app treats as a
/// contract, so the app owns no migrations for it.
/// </summary>
public sealed class RdwDbContext(DbContextOptions<RdwDbContext> options) : DbContext(options)
{
    public DbSet<RdwVehicleRow> Vehicles => Set<RdwVehicleRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<RdwVehicleRow>(vehicle =>
        {
            vehicle.HasNoKey();
            vehicle.ToView(RdwVehicleLookup.RequiredView);
            vehicle.Property(row => row.NormalizedPlate).HasColumnName("normalized_plate");
            vehicle.Property(row => row.Make).HasColumnName("make");
            vehicle.Property(row => row.Model).HasColumnName("model");
            vehicle.Property(row => row.CatalogPrice).HasColumnName("catalog_price").HasConversion<double>();
            vehicle.Property(row => row.RegistrationYear).HasColumnName("registration_year");
            vehicle.Property(row => row.FuelDescription).HasColumnName("fuel_description");
            vehicle.Property(row => row.BodyType).HasColumnName("body_type");
        });
    }
}
