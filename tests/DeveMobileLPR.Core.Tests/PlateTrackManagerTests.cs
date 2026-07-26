using DeveMobileLPR.Geometry;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Tests;

public sealed class PlateTrackManagerTests
{
    [Fact]
    public void Update_ConfirmsOnceAfterThreeAssociatedFrames()
    {
        var manager = new PlateTrackManager();

        Assert.Empty(manager.Update(Frame(1, 10)));
        Assert.Empty(manager.Update(Frame(2, 12)));
        var confirmed = manager.Update(Frame(3, 14));
        Assert.Empty(manager.Update(Frame(4, 16)));

        var result = Assert.Single(confirmed);
        Assert.Equal("AB1234", result.Consensus.NormalizedPlate);
        Assert.Equal("AB-12-34", result.Consensus.DisplayPlate);
        Assert.Equal(3, result.Consensus.ObservationCount);
    }

    [Fact]
    public void Update_ExpiresAStaleTrackInsteadOfCombiningSeparateEncounters()
    {
        var manager = new PlateTrackManager();
        Assert.Empty(manager.Update(Frame(1, 10)));
        Assert.Empty(manager.Update(Frame(2, 12)));

        var muchLater = new FrameRecognition(
            3,
            DateTimeOffset.UnixEpoch.AddSeconds(10),
            TimeSpan.Zero,
            [Observation(3, 14, DateTimeOffset.UnixEpoch.AddSeconds(10))]);

        Assert.Empty(manager.Update(muchLater));
    }

    private static FrameRecognition Frame(long sequence, float left)
    {
        var capturedAt = DateTimeOffset.UnixEpoch.AddMilliseconds(sequence * 200);
        return new FrameRecognition(sequence, capturedAt, TimeSpan.FromMilliseconds(50), [Observation(sequence, left, capturedAt)]);
    }

    private static PlateObservation Observation(long sequence, float left, DateTimeOffset capturedAt)
    {
        const string text = "AB1234";
        var characters = text
            .Select(character => new CharacterHypothesis([new CharacterCandidate(character, 0.95f)]))
            .ToArray();
        return new PlateObservation(
            sequence,
            capturedAt,
            new PlateDetection(new BoundingBox(left, 10, left + 100, 40), 0.95f),
            new PlateRead(text, 0.95f, characters, "Netherlands", 0.98f),
            0.9f);
    }
}
