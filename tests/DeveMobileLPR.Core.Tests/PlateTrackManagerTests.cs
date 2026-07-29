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
            [Observation(3, 14, DateTimeOffset.UnixEpoch.AddSeconds(10))]);

        Assert.Empty(manager.Update(muchLater));
    }

    [Fact]
    public void Update_AssociatesExactTextAcrossNonOverlappingLowFrameRateMovement()
    {
        var manager = new PlateTrackManager();
        var start = DateTimeOffset.UnixEpoch;

        var first = manager.UpdateDetailed(Frame(1, start, Observation(1, "AB1234", 100, start)));
        var second = manager.UpdateDetailed(Frame(2, start.AddMilliseconds(500), Observation(2, "AB1234", 800, start.AddMilliseconds(500))));
        var third = manager.UpdateDetailed(Frame(3, start.AddSeconds(1), Observation(3, "AB1234", 1500, start.AddSeconds(1))));

        var trackId = Assert.Single(first.Associations).TrackId;
        Assert.Equal(PlateAssociationKind.NewTrack, first.Associations[0].Kind);
        Assert.Equal(trackId, Assert.Single(second.Associations).TrackId);
        Assert.Equal(PlateAssociationKind.ExactText, second.Associations[0].Kind);
        Assert.Equal(0, second.Associations[0].IntersectionOverUnion);
        Assert.Equal(trackId, Assert.Single(third.Associations).TrackId);
        Assert.Equal(PlateAssociationKind.ExactText, third.Associations[0].Kind);
        Assert.Single(third.Confirmations);
        Assert.Single(third.Tracks);
    }

    [Fact]
    public void Update_DoesNotAssociateExactTextAcrossImplausibleJump()
    {
        var manager = new PlateTrackManager();
        var start = DateTimeOffset.UnixEpoch;

        var first = manager.UpdateDetailed(Frame(1, start, Observation(1, "AB1234", 0, start)));
        var second = manager.UpdateDetailed(Frame(2, start.AddMilliseconds(500), Observation(2, "AB1234", 3500, start.AddMilliseconds(500))));

        Assert.NotEqual(Assert.Single(first.Associations).TrackId, Assert.Single(second.Associations).TrackId);
        Assert.Equal(PlateAssociationKind.NewTrack, second.Associations[0].Kind);
        Assert.Equal(2, second.Tracks.Count);
    }

    [Fact]
    public void Update_AssociatesSingleCharacterVariationWithCloseGeometry()
    {
        var manager = new PlateTrackManager();
        var start = DateTimeOffset.UnixEpoch;

        var first = manager.UpdateDetailed(Frame(1, start, Observation(1, "AB1234", 100, start)));
        var close = manager.UpdateDetailed(Frame(2, start.AddMilliseconds(500), Observation(2, "AB1235", 130, start.AddMilliseconds(500))));

        Assert.Equal(Assert.Single(first.Associations).TrackId, Assert.Single(close.Associations).TrackId);
        Assert.Equal(PlateAssociationKind.SimilarText, close.Associations[0].Kind);
        Assert.Equal(1, close.Associations[0].TextEditDistance);
    }

    [Fact]
    public void Update_DoesNotAssociateSingleCharacterVariationAcrossImplausibleMovement()
    {
        var manager = new PlateTrackManager();
        var start = DateTimeOffset.UnixEpoch;

        var first = manager.UpdateDetailed(Frame(1, start, Observation(1, "AB1234", 100, start), 1000, 500));
        var far = manager.UpdateDetailed(Frame(2, start.AddMilliseconds(500), Observation(2, "AB1235", 800, start.AddMilliseconds(500)), 1000, 500));

        Assert.NotEqual(Assert.Single(first.Associations).TrackId, Assert.Single(far.Associations).TrackId);
        Assert.Equal(PlateAssociationKind.NewTrack, far.Associations[0].Kind);
    }

    [Fact]
    public void Update_UsesPredictedMotionWhenTheLatestBoxesDoNotOverlap()
    {
        var manager = new PlateTrackManager();
        var start = DateTimeOffset.UnixEpoch;

        var first = manager.UpdateDetailed(Frame(1, start, Observation(1, "AB1234", 100, start), 1000, 500));
        var second = manager.UpdateDetailed(Frame(2, start.AddMilliseconds(500), Observation(2, "AB1234", 200, start.AddMilliseconds(500)), 1000, 500));
        var third = manager.UpdateDetailed(Frame(3, start.AddSeconds(1), Observation(3, "AB1", 300, start.AddSeconds(1)), 1000, 500));

        var trackId = Assert.Single(first.Associations).TrackId;
        Assert.Equal(trackId, Assert.Single(second.Associations).TrackId);
        Assert.Equal(trackId, Assert.Single(third.Associations).TrackId);
        Assert.Equal(PlateAssociationKind.PredictedMotion, third.Associations[0].Kind);
        Assert.Equal(0, third.Associations[0].IntersectionOverUnion);
        Assert.True(third.Associations[0].PredictedIntersectionOverUnion > 0.9f);
    }

    [Fact]
    public void Update_ScalesMotionPredictionByElapsedTime()
    {
        var manager = new PlateTrackManager(new TrackingOptions(
            MaximumPredictedCenterDistanceInPlateWidths: 0.25f));
        var start = DateTimeOffset.UnixEpoch;

        manager.UpdateDetailed(Frame(1, start, Observation(1, "AB1234", 100, start), 1000, 500));
        manager.UpdateDetailed(Frame(2, start.AddMilliseconds(500), Observation(2, "AB1234", 200, start.AddMilliseconds(500)), 1000, 500));
        var third = manager.UpdateDetailed(Frame(3, start.AddMilliseconds(1500), Observation(3, "AB1", 400, start.AddMilliseconds(1500)), 1000, 500));

        Assert.Equal(PlateAssociationKind.PredictedMotion, Assert.Single(third.Associations).Kind);
        Assert.True(third.Associations[0].PredictedIntersectionOverUnion > 0.9f);
    }

    [Fact]
    public void Update_DoesNotLetMotionOverrideConflictingFullPlateIdentities()
    {
        var manager = new PlateTrackManager();
        var start = DateTimeOffset.UnixEpoch;

        manager.UpdateDetailed(Frame(1, start, Observation(1, "AB1234", 100, start), 1000, 500));
        manager.UpdateDetailed(Frame(2, start.AddMilliseconds(500), Observation(2, "AB1234", 200, start.AddMilliseconds(500)), 1000, 500));
        var conflicting = manager.UpdateDetailed(Frame(3, start.AddSeconds(1), Observation(3, "ZX9876", 300, start.AddSeconds(1)), 1000, 500));

        Assert.Equal(PlateAssociationKind.NewTrack, Assert.Single(conflicting.Associations).Kind);
        Assert.Equal(2, conflicting.Tracks.Count);
    }

    [Fact]
    public void Update_PreservesDistinctTextIdentitiesWhenTracksCross()
    {
        var manager = new PlateTrackManager();
        var start = DateTimeOffset.UnixEpoch;

        var first = manager.UpdateDetailed(Frame(
            1,
            start,
            Observation(1, "AB1234", 400, start),
            Observation(1, "CD5678", 1000, start)));
        var abTrack = first.Associations[0].TrackId;
        var cdTrack = first.Associations[1].TrackId;

        var crossed = manager.UpdateDetailed(Frame(
            2,
            start.AddMilliseconds(500),
            Observation(2, "AB1234", 1000, start.AddMilliseconds(500)),
            Observation(2, "CD5678", 400, start.AddMilliseconds(500))));

        Assert.Equal(abTrack, crossed.Associations[0].TrackId);
        Assert.Equal(cdTrack, crossed.Associations[1].TrackId);
        Assert.All(crossed.Associations, association => Assert.Equal(PlateAssociationKind.ExactText, association.Kind));
    }

    private static FrameRecognition Frame(long sequence, float left)
    {
        var capturedAt = DateTimeOffset.UnixEpoch.AddMilliseconds(sequence * 200);
        return Frame(sequence, capturedAt, Observation(sequence, "AB1234", left, capturedAt));
    }

    private static FrameRecognition Frame(
        long sequence,
        DateTimeOffset capturedAt,
        PlateObservation observation,
        int width = 3840,
        int height = 2160) => Frame(sequence, capturedAt, [observation], width, height);

    private static FrameRecognition Frame(
        long sequence,
        DateTimeOffset capturedAt,
        PlateObservation first,
        PlateObservation second) => Frame(sequence, capturedAt, [first, second], 3840, 2160);

    private static FrameRecognition Frame(
        long sequence,
        DateTimeOffset capturedAt,
        IReadOnlyList<PlateObservation> observations,
        int width,
        int height) => new(sequence, capturedAt, observations)
        {
            SourceWidth = width,
            SourceHeight = height
        };

    private static PlateObservation Observation(long sequence, float left, DateTimeOffset capturedAt)
        => Observation(sequence, "AB1234", left, capturedAt);

    private static PlateObservation Observation(long sequence, string text, float left, DateTimeOffset capturedAt)
    {
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
