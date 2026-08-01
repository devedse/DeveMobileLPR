using DeveMobileLPR.Geometry;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Tests;

public sealed class TemporalConsensusTests
{
    [Fact]
    public void Resolve_ConfirmsThreeConsistentDutchReads()
    {
        var consensus = new TemporalConsensus();
        var observations = new[]
        {
            Observation(1, "AB1234", 0.96f),
            Observation(2, "AB1234", 0.93f),
            Observation(3, "AB1234", 0.91f)
        };

        var result = consensus.Resolve(observations);

        Assert.NotNull(result);
        Assert.Equal("AB1234", result.NormalizedPlate);
        Assert.Equal("Netherlands", result.Region);
        Assert.Equal(3, result.ObservationCount);
    }

    [Fact]
    public void Resolve_DoesNotConfirmSingleOrConflictingReads()
    {
        var consensus = new TemporalConsensus();
        Assert.Null(consensus.Resolve([Observation(1, "AB1234", 0.99f)]));
        Assert.Null(consensus.Resolve([
            Observation(1, "AB1234", 0.9f),
            Observation(2, "AB1235", 0.9f),
            Observation(3, "AB1236", 0.9f)]));
    }

    [Fact]
    public void Resolve_StrongExactPairIsEnabledByDefault()
    {
        var consensus = new TemporalConsensus();

        var result = consensus.Resolve([
            Observation(1, "AB1234", 0.99f),
            Observation(2, "AB1234", 0.98f)]);

        Assert.NotNull(result);
        Assert.Equal("AB1234", result.NormalizedPlate);
        Assert.Equal(2, result.ObservationCount);
    }

    [Fact]
    public void Resolve_StrongExactPairCanBeDisabledExplicitly()
    {
        var consensus = new TemporalConsensus(new RecognitionTuningConfiguration
        {
            StrongPair_Enabled = false
        });

        Assert.Null(consensus.Resolve([
            Observation(1, "AB1234", 0.99f),
            Observation(2, "AB1234", 0.98f)]));
    }

    [Fact]
    public void Resolve_StrongExactPairRejectsWeakOrAmbiguousEvidence()
    {
        var consensus = new TemporalConsensus(new RecognitionTuningConfiguration
        {
            StrongPair_Enabled = true
        });
        var weak = Observation(2, "AB1234", 0.98f) with
        {
            Detection = new PlateDetection(new BoundingBox(10, 10, 110, 40), 0.20f)
        };
        var lowOcrConfidence = Observation(2, "AB1234", 0.94f);
        var lowQuality = Observation(2, "AB1234", 0.98f) with { Quality = 0.69f };
        var ambiguousCharacters = "AB1234"
            .Select(character => new CharacterHypothesis([
                new CharacterCandidate(character, 0.92f),
                new CharacterCandidate(character == 'A' ? 'B' : 'A', 0.60f)]))
            .ToArray();
        var ambiguous = Observation(2, "AB1234", 0.98f) with
        {
            Read = new PlateRead("AB1234", 0.98f, ambiguousCharacters, "Netherlands", 0.98f)
        };

        Assert.Null(consensus.Resolve([Observation(1, "AB1234", 0.99f), weak]));
        Assert.Null(consensus.Resolve([Observation(1, "AB1234", 0.99f), lowOcrConfidence]));
        Assert.Null(consensus.Resolve([Observation(1, "AB1234", 0.99f), lowQuality]));
        Assert.Null(consensus.Resolve([Observation(1, "AB1234", 0.99f), ambiguous]));
    }

    [Fact]
    public void Resolve_StrongExactPairRejectsConflictingDuplicateFrameOrInvalidDutchText()
    {
        var consensus = new TemporalConsensus(new RecognitionTuningConfiguration
        {
            StrongPair_Enabled = true
        });

        Assert.Null(consensus.Resolve([
            Observation(1, "AB1234", 0.99f),
            Observation(2, "AB1235", 0.99f)]));
        Assert.Null(consensus.Resolve([
            Observation(1, "AB1234", 0.99f),
            Observation(1, "AB1234", 0.99f)]));
        Assert.Null(consensus.Resolve([
            Observation(1, "AAAAAA", 0.99f),
            Observation(2, "AAAAAA", 0.99f)]));
        Assert.Null(consensus.Resolve([
            ForeignObservation(1, "K183", 0.99f),
            ForeignObservation(2, "K183", 0.99f)]));
    }

    private static PlateObservation Observation(long sequence, string text, float confidence)
    {
        var characters = text
            .Select(character => new CharacterHypothesis([new CharacterCandidate(character, confidence)]))
            .ToArray();
        return new PlateObservation(
            sequence,
            DateTimeOffset.UnixEpoch.AddMilliseconds(sequence),
            new PlateDetection(new BoundingBox(10, 10, 110, 40), 0.95f),
            new PlateRead(text, confidence, characters, "Netherlands", 0.98f),
            0.9f);
    }

    private static PlateObservation ForeignObservation(long sequence, string text, float confidence)
    {
        var observation = Observation(sequence, text, confidence);
        return observation with
        {
            Read = observation.Read with { Region = "United States" }
        };
    }
}
