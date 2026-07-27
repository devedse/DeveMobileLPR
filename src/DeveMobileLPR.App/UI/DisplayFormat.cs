using System.Globalization;

namespace DeveMobileLPR.App.UI;

internal static class DisplayFormat
{
    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    public static string Price(decimal? value) => value is null ? "Unknown value" : value.Value.ToString("C0", Dutch);
    public static string CompactPrice(decimal? value) => value is null ? "—" : value.Value >= 1_000_000 ? $"€{value.Value / 1_000_000:0.#}m" : value.Value >= 1_000 ? $"€{value.Value / 1_000:0}k" : $"€{value.Value:0}";
    public static string Distance(double meters) => meters >= 1000 ? $"{meters / 1000:0.0} km" : $"{meters:0} m";
    public static string Duration(TimeSpan value) => value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m" : value.TotalMinutes >= 1 ? $"{(int)value.TotalMinutes} min" : $"{Math.Max(0, (int)value.TotalSeconds)} sec";
    public static string Relative(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        if (local.Date == today) return $"Today, {local:t}";
        if (local.Date == today.AddDays(-1)) return $"Yesterday, {local:t}";
        return local.ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture);
    }
}
