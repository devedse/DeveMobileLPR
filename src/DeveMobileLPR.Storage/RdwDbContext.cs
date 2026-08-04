using Microsoft.EntityFrameworkCore;

namespace DeveMobileLPR.Storage;

public sealed class RdwDbContext(DbContextOptions<RdwDbContext> options) : DbContext(options)
{
    public DbSet<RdwVehicleEntity> Vehicles => Set<RdwVehicleEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var vehicle = modelBuilder.Entity<RdwVehicleEntity>();
        vehicle.ToView("rdw_vehicles");
        vehicle.HasKey(v => v.NormalizedPlate);
        vehicle.Property(v => v.NormalizedPlate).HasColumnName("normalized_plate");
        vehicle.Property(v => v.Make).HasColumnName("make");
        vehicle.Property(v => v.Model).HasColumnName("model");
        vehicle.Property(v => v.CatalogPrice).HasColumnName("catalog_price").HasColumnType("INTEGER");
        vehicle.Property(v => v.RegistrationYear).HasColumnName("registration_year");
        vehicle.Property(v => v.FuelDescription).HasColumnName("fuel_description");
        vehicle.Property(v => v.BodyType).HasColumnName("body_type");
    }
}

public sealed class RdwVehicleEntity
{
    public required string NormalizedPlate { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public decimal? CatalogPrice { get; set; }
    public int? RegistrationYear { get; set; }
    public string? FuelDescription { get; set; }
    public string? BodyType { get; set; }
}
