namespace DeveMobileLPR.RdwDownloader;

internal static class RdwDatasets
{
    public const string Vehicles = "m9d7-ebf2";
    public const string Fuels = "8ys7-d773";

    public static readonly IReadOnlySet<string> RequiredVehicleFields = new HashSet<string>(
        ["kenteken", "merk", "handelsbenaming", "catalogusprijs", "datum_eerste_toelating", "inrichting"],
        StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> RequiredFuelFields = new HashSet<string>(
        ["kenteken", "brandstof_volgnummer", "brandstof_omschrijving"],
        StringComparer.Ordinal);
}
