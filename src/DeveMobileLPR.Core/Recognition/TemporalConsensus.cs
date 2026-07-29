namespace DeveMobileLPR.Recognition;

public sealed record ConsensusOptions(
    int MinimumObservations = 3,
    float MinimumConfidence = 0.78f,
    float MinimumWinnerMargin = 0.12f,
    float MinimumCharacterConfidence = 0.60f,
    bool RequirePlausibleDutchFormatForDutchRegion = true);

public sealed class TemporalConsensus(ConsensusOptions? options = null)
{
    private readonly ConsensusOptions _options = options ?? new ConsensusOptions();

    public ConsensusResult? Resolve(IReadOnlyCollection<PlateObservation> observations)
    {
        if (observations.Count < _options.MinimumObservations)
        {
            return null;
        }

        var candidates = observations
            .Select(observation => new WeightedRead(observation, PlateText.Normalize(observation.Read.Text), PlateEvidence.Weight(observation)))
            .Where(static candidate => candidate.Text.Length is >= 4 and <= 10)
            .ToArray();

        if (candidates.Length < _options.MinimumObservations)
        {
            return null;
        }

        var exactGroups = candidates
            .GroupBy(static candidate => candidate.Text, StringComparer.Ordinal)
            .Select(group => new
            {
                Text = group.Key,
                Weight = group.Sum(static item => item.Weight),
                Count = group.Select(static item => item.Observation.FrameSequence).Distinct().Count(),
                Reads = group.ToArray()
            })
            .OrderByDescending(static group => group.Weight)
            .ToArray();

        var totalWeight = Math.Max(0.0001f, candidates.Sum(static candidate => candidate.Weight));
        var winner = exactGroups[0];
        var winnerShare = winner.Weight / totalWeight;
        var runnerShare = exactGroups.Length > 1 ? exactGroups[1].Weight / totalWeight : 0;

        if (winner.Count >= _options.MinimumObservations &&
            winnerShare >= _options.MinimumConfidence &&
            winnerShare - runnerShare >= _options.MinimumWinnerMargin)
        {
            return BuildResult(winner.Text, winnerShare, winner.Count, winner.Reads.Select(static item => item.Observation));
        }

        return ResolvePerCharacter(candidates);
    }

    private ConsensusResult? ResolvePerCharacter(IReadOnlyCollection<WeightedRead> reads)
    {
        var winningLength = reads
            .GroupBy(static read => read.Text.Length)
            .Select(group => new { Length = group.Key, Weight = group.Sum(static item => item.Weight) })
            .OrderByDescending(static group => group.Weight)
            .First().Length;
        var compatible = reads.Where(read => read.Text.Length == winningLength).ToArray();
        if (compatible.Length < _options.MinimumObservations)
        {
            return null;
        }

        var result = new char[winningLength];
        var positionConfidence = new float[winningLength];
        for (var position = 0; position < winningLength; position++)
        {
            var scores = new Dictionary<char, float>();
            foreach (var read in compatible)
            {
                if (position < read.Observation.Read.Characters.Count)
                {
                    foreach (var candidate in read.Observation.Read.Characters[position].Candidates)
                    {
                        scores[candidate.Character] = scores.GetValueOrDefault(candidate.Character) + candidate.Probability * read.Weight;
                    }
                }
                else
                {
                    var character = read.Text[position];
                    scores[character] = scores.GetValueOrDefault(character) + read.Weight;
                }
            }

            var ordered = scores.OrderByDescending(static pair => pair.Value).ToArray();
            result[position] = ordered[0].Key;
            var sum = Math.Max(0.0001f, ordered.Sum(static pair => pair.Value));
            positionConfidence[position] = ordered[0].Value / sum;
        }

        var text = PlateText.Normalize(new string(result));
        var confidence = positionConfidence.Length == 0 ? 0 : positionConfidence.Average();
        var supportingFrames = compatible.Count(read => PlateText.EditDistance(read.Text, text) <= 1);
        if (supportingFrames < _options.MinimumObservations ||
            confidence < _options.MinimumConfidence ||
            positionConfidence.Any(value => value < _options.MinimumCharacterConfidence))
        {
            return null;
        }

        return BuildResult(text, confidence, supportingFrames, compatible.Select(static read => read.Observation));
    }

    private ConsensusResult? BuildResult(string text, float confidence, int count, IEnumerable<PlateObservation> observations)
    {
        var materialized = observations.ToArray();
        var region = materialized
            .Where(static observation => !string.IsNullOrWhiteSpace(observation.Read.Region))
            .GroupBy(static observation => observation.Read.Region, StringComparer.Ordinal)
            .OrderByDescending(static group => group.Sum(item => item.Read.RegionConfidence ?? 0.5f))
            .Select(static group => group.Key)
            .FirstOrDefault();

        if (_options.RequirePlausibleDutchFormatForDutchRegion &&
            string.Equals(region, "Netherlands", StringComparison.Ordinal) &&
            !PlateText.IsPlausibleDutchPlate(text))
        {
            return null;
        }

        var display = string.Equals(region, "Netherlands", StringComparison.Ordinal)
            ? PlateText.FormatDutchPlate(text)
            : text;
        return new ConsensusResult(text, display, region, confidence, count);
    }

    private sealed record WeightedRead(PlateObservation Observation, string Text, float Weight);
}
