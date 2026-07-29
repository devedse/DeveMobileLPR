namespace DeveMobileLPR.Recognition;

internal static class PlateEvidence
{
    public static float Weight(PlateObservation observation) =>
        Math.Clamp(observation.Detection.Confidence, 0, 1)
        * Math.Clamp(observation.Read.Confidence, 0, 1)
        * Math.Clamp(observation.Quality, 0.1f, 1);

    public static string StableText(IEnumerable<PlateObservation> observations) =>
        observations
            .Select(observation => new
            {
                Text = PlateText.Normalize(observation.Read.Text),
                Weight = Weight(observation)
            })
            .Where(static item => item.Text.Length > 0)
            .GroupBy(static item => item.Text, StringComparer.Ordinal)
            .Select(group => new
            {
                Text = group.Key,
                Weight = group.Sum(static item => item.Weight)
            })
            .OrderByDescending(static item => item.Weight)
            .ThenBy(static item => item.Text, StringComparer.Ordinal)
            .Select(static item => item.Text)
            .FirstOrDefault() ?? string.Empty;
}
