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
}
