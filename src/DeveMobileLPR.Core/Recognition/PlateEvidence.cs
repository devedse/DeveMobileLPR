namespace DeveMobileLPR.Recognition;

internal static class PlateEvidence
{
    public static float Weight(
        PlateObservation observation,
        RecognitionTuningConfiguration configuration) =>
        Math.Clamp(observation.Detection.Confidence, 0, 1)
        * Math.Clamp(observation.Read.Confidence, 0, 1)
        * Math.Clamp(observation.Quality, configuration.Consensus_MinimumQualityWeight, 1);

    public static string StableText(
        IEnumerable<PlateObservation> observations,
        RecognitionTuningConfiguration configuration) =>
        observations
            .Select(observation => new
            {
                Text = PlateText.Normalize(observation.Read.Text),
                Weight = Weight(observation, configuration)
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

    public static bool HasStrongCharacterEvidence(
        PlateObservation observation,
        float minimumProbability,
        float minimumMargin)
    {
        var text = PlateText.Normalize(observation.Read.Text);
        if (text.Length == 0 || observation.Read.Characters.Count < text.Length)
        {
            return false;
        }

        for (var position = 0; position < text.Length; position++)
        {
            var candidates = observation.Read.Characters[position].Candidates;
            var selected = candidates
                .Where(candidate => char.ToUpperInvariant(candidate.Character) == text[position])
                .Select(static candidate => candidate.Probability)
                .DefaultIfEmpty(float.NegativeInfinity)
                .Max();
            var runnerUp = candidates
                .Where(candidate => char.ToUpperInvariant(candidate.Character) != text[position])
                .Select(static candidate => candidate.Probability)
                .DefaultIfEmpty(0)
                .Max();
            if (selected < minimumProbability || selected - runnerUp < minimumMargin)
            {
                return false;
            }
        }

        return true;
    }
}
