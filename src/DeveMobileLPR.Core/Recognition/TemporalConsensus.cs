namespace DeveMobileLPR.Recognition;

public sealed class TemporalConsensus
{
    private readonly RecognitionTuningConfiguration _configuration;

    public TemporalConsensus(RecognitionTuningConfiguration? configuration = null)
    {
        _configuration = configuration ?? new RecognitionTuningConfiguration();
        _configuration.Validate();
    }

    public ConsensusResult? Resolve(IReadOnlyCollection<PlateObservation> observations)
    {
        if (observations.Count < _configuration.Consensus_MinimumObservations)
        {
            return ResolveStrongExactPair(observations);
        }

        var candidates = observations
            .Select(observation => new WeightedRead(
                observation,
                PlateText.Normalize(observation.Read.Text),
                PlateEvidence.Weight(observation, _configuration)))
            .Where(candidate => candidate.Text.Length >= _configuration.Consensus_MinimumPlateLength
                && candidate.Text.Length <= _configuration.Consensus_MaximumPlateLength)
            .ToArray();

        if (candidates.Length < _configuration.Consensus_MinimumObservations)
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

        if (winner.Count >= _configuration.Consensus_MinimumObservations &&
            winnerShare >= _configuration.Consensus_MinimumWinnerShare &&
            winnerShare - runnerShare >= _configuration.Consensus_MinimumWinnerMargin)
        {
            return BuildResult(winner.Text, winnerShare, winner.Count, winner.Reads.Select(static item => item.Observation));
        }

        return ResolvePerCharacter(candidates);
    }

    private ConsensusResult? ResolveStrongExactPair(IReadOnlyCollection<PlateObservation> observations)
    {
        // This fast path is intentionally narrower than normal consensus. It is
        // for short-lived Dutch plates that cannot physically produce a third AI
        // frame on slower phones, not a general reduction of the evidence count.
        if (!_configuration.StrongPair_Enabled
            || observations.Count != _configuration.StrongPair_RequiredDistinctFrames)
        {
            return null;
        }

        var pair = observations.ToArray();
        if (pair.Select(static observation => observation.FrameSequence).Distinct().Count()
            != _configuration.StrongPair_RequiredDistinctFrames)
        {
            return null;
        }

        var text = PlateText.Normalize(pair[0].Read.Text);
        if (text.Length < _configuration.Consensus_MinimumPlateLength
            || text.Length > _configuration.Consensus_MaximumPlateLength
            || !string.Equals(text, PlateText.Normalize(pair[1].Read.Text), StringComparison.Ordinal)
            || pair.Skip(1).Any(observation =>
                !string.Equals(text, PlateText.Normalize(observation.Read.Text), StringComparison.Ordinal))
            || (_configuration.StrongPair_RequirePlausibleDutchFormat
                && !PlateText.IsPlausibleDutchPlate(text))
            || pair.Any(observation => !IsStrongExactPairObservation(observation)))
        {
            return null;
        }

        var confidence = pair.Average(static observation => observation.Read.Confidence);
        return BuildResult(text, confidence, pair.Length, pair);
    }

    private bool IsStrongExactPairObservation(PlateObservation observation) =>
        observation.Read.Confidence >= _configuration.StrongPair_MinimumOcrConfidence
        && observation.Quality >= _configuration.StrongPair_MinimumQuality
        && PlateEvidence.Weight(observation, _configuration) >= _configuration.StrongPair_MinimumEvidenceWeight
        && PlateEvidence.HasStrongCharacterEvidence(
            observation,
            _configuration.StrongPair_MinimumCharacterProbability,
            _configuration.StrongPair_MinimumCharacterMargin);

    private ConsensusResult? ResolvePerCharacter(IReadOnlyCollection<WeightedRead> reads)
    {
        var winningLength = reads
            .GroupBy(static read => read.Text.Length)
            .Select(group => new { Length = group.Key, Weight = group.Sum(static item => item.Weight) })
            .OrderByDescending(static group => group.Weight)
            .First().Length;
        var compatible = reads.Where(read => read.Text.Length == winningLength).ToArray();
        if (compatible.Length < _configuration.Consensus_MinimumObservations)
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
        var supportingFrames = compatible.Count(read =>
            PlateText.EditDistance(read.Text, text) <= _configuration.Consensus_MaximumSupportingEditDistance);
        if (supportingFrames < _configuration.Consensus_MinimumObservations ||
            confidence < _configuration.Consensus_MinimumWinnerShare ||
            positionConfidence.Any(value => value < _configuration.Consensus_MinimumCharacterConfidence))
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

        if (_configuration.Consensus_RequirePlausibleDutchFormatForDutchRegion &&
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
