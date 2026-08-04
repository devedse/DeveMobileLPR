using DeveMobileLPR.Storage.Models;
using Microsoft.EntityFrameworkCore;

namespace DeveMobileLPR.Storage;

/// <summary>
/// Read-only model over the separate RDW database. It is deliberately not Code First: the file is
/// produced by the downloader and only ever consumed here, through a view the app treats as a
/// contract, so the app owns no migrations for it.
/// </summary>
public sealed class RdwDbContext(DbContextOptions<RdwDbContext> options) : DbContext(options)
{
    public DbSet<RdwVehicleModel> Vehicles => Set<RdwVehicleModel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<RdwVehicleModel>(vehicle =>
        {
            vehicle.HasNoKey();
            vehicle.ToView(RdwVehicleLookup.RequiredView);
            vehicle.Property(model => model.NormalizedPlate).HasColumnName("normalized_plate");
            vehicle.Property(model => model.Make).HasColumnName("make");
            vehicle.Property(model => model.Model).HasColumnName("model");
            vehicle.Property(model => model.CatalogPrice).HasColumnName("catalog_price").HasConversion<double>();
            vehicle.Property(model => model.RegistrationYear).HasColumnName("registration_year");
            vehicle.Property(model => model.FuelDescription).HasColumnName("fuel_description");
            vehicle.Property(model => model.BodyType).HasColumnName("body_type");
        });
    }
}
