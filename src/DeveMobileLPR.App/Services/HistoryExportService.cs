using System.Globalization;
using System.Text;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.Services;

internal sealed class HistoryExportService(ISightingRepository repository)
{
    public async Task<string> CreateCsvAsync(CancellationToken cancellationToken)
    {
        var sightings = await repository.GetAllSightingsAsync(cancellationToken);
        var path = Path.Combine(FileSystem.CacheDirectory, $"devemobilelpr-history-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var csv = new StringBuilder("trip_id,plate,first_seen,last_seen,confidence,reads,latitude,longitude,make,model,catalog_price,registration_year,fuel,body_type\r\n");
        foreach (var sighting in sightings)
        {
            csv.Append(Cell(sighting.TripId?.ToString(CultureInfo.InvariantCulture))).Append(',')
               .Append(Cell(sighting.DisplayPlate)).Append(',')
               .Append(Cell(sighting.FirstSeenAt.ToString("O", CultureInfo.InvariantCulture))).Append(',')
               .Append(Cell(sighting.LastSeenAt.ToString("O", CultureInfo.InvariantCulture))).Append(',')
               .Append(Cell(sighting.Confidence.ToString(CultureInfo.InvariantCulture))).Append(',')
               .Append(Cell(sighting.ObservationCount.ToString(CultureInfo.InvariantCulture))).Append(',')
               .Append(Cell(sighting.Location?.Latitude.ToString(CultureInfo.InvariantCulture))).Append(',')
               .Append(Cell(sighting.Location?.Longitude.ToString(CultureInfo.InvariantCulture))).Append(',')
               .Append(Cell(sighting.Vehicle?.Make)).Append(',')
               .Append(Cell(sighting.Vehicle?.Model)).Append(',')
               .Append(Cell(sighting.Vehicle?.CatalogPrice?.ToString(CultureInfo.InvariantCulture))).Append(',')
               .Append(Cell(sighting.Vehicle?.RegistrationYear?.ToString(CultureInfo.InvariantCulture))).Append(',')
               .Append(Cell(sighting.Vehicle?.FuelDescription)).Append(',')
               .Append(Cell(sighting.Vehicle?.BodyType)).Append("\r\n");
        }
        await File.WriteAllTextAsync(path, csv.ToString(), new UTF8Encoding(true), cancellationToken);
        return path;
    }

    private static string Cell(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
