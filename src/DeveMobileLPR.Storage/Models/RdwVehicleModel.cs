namespace DeveMobileLPR.Storage.Models;

/// <summary>One row of the downloader's stable <c>rdw_vehicles</c> view.</summary>
public sealed class RdwVehicleModel
{
    public string NormalizedPlate { get; set; } = null!;
    public string? Make { get; set; }
    public string? Model { get; set; }
    public decimal? CatalogPrice { get; set; }
    public int? RegistrationYear { get; set; }
    public string? FuelDescription { get; set; }
    public string? BodyType { get; set; }
}
