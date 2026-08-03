using DeveMobileLPR.Geometry;

namespace DeveMobileLPR.Recognition;

public sealed class PlateTrackManager
{
    private readonly RecognitionTuningConfiguration _configuration;
    private readonly TemporalConsensus _consensus;
    private readonly List<Track> _tracks = [];

    public PlateTrackManager(RecognitionTuningConfiguration? configuration = null)
    {
        _configuration = configuration ?? new RecognitionTuningConfiguration();
        _configuration.Validate();
        _consensus = new TemporalConsensus(_configuration);
    }

    public IReadOnlyList<ConfirmedPlate> Update(FrameRecognition recognition)
        => UpdateDetailed(recognition).Confirmations;

    public PlateTrackingUpdate UpdateDetailed(FrameRecognition recognition)
    {
        Expire(recognition.CapturedAt);
        var observations = recognition.Observations.ToArray();
        var matches = Associate(recognition, observations);
        var confirmations = new List<ConfirmedPlate>();
        var associations = new PlateTrackAssociation[observations.Length];

        for (var observationIndex = 0; observationIndex < observations.Length; observationIndex++)
        {
            var observation = observations[observationIndex];
            var match = matches[observationIndex];
            Track track;
            if (match is null)
            {
                track = new Track(Guid.NewGuid(), observation.CapturedAt, observation.Detection.Bounds, _configuration);
                _tracks.Add(track);
                associations[observationIndex] = new PlateTrackAssociation(
                    track.Id,
                    recognition.FrameSequence,
                    true,
                    null)
                {
                    Kind = PlateAssociationKind.NewTrack
                };
            }
            else
            {
                track = match.Track;
                associations[observationIndex] = new PlateTrackAssociation(
                    track.Id,
                    recognition.FrameSequence,
                    false,
                    match.LastIntersectionOverUnion)
                {
                    Kind = match.Kind,
                    PredictedBounds = match.PredictedBounds,
                    PredictedIntersectionOverUnion = match.PredictedIntersectionOverUnion,
                    FrameCenterDistance = match.FrameCenterDistance,
                    ScaleRatio = match.ScaleRatio,
                    TextEditDistance = match.TextEditDistance,
                    Score = match.Score
                };
            }

            track.Add(observation, _configuration.Tracking_MaximumObservationsPerTrack);
            if (_consensus.Resolve(track.Observations) is { } result
                && track.ShouldPublish(result))
            {
                track.Publish(result);
                confirmations.Add(new ConfirmedPlate(
                    track.Id,
                    track.FirstSeenAt,
                    track.LastSeenAt,
                    track.LastBounds,
                    result)
                {
                    Revision = track.Revision
                });
            }
        }

        return new PlateTrackingUpdate(
            confirmations,
            _tracks.Select(static track => track.ToSnapshot()).ToArray(),
            associations);
    }

    public void Reset() => _tracks.Clear();

    private AssociationCandidate?[] Associate(
        FrameRecognition recognition,
        IReadOnlyList<PlateObservation> observations)
    {
        var result = new AssociationCandidate?[observations.Count];
        if (observations.Count == 0 || _tracks.Count == 0)
        {
            return result;
        }

        var unassignedObservations = new HashSet<int>(Enumerable.Range(0, observations.Count));
        var unassignedTracks = new HashSet<Guid>(_tracks.Select(static track => track.Id));
        AssignTier(AssociationTier.ExactText, recognition, observations, result, unassignedObservations, unassignedTracks);
        AssignTier(AssociationTier.SimilarText, recognition, observations, result, unassignedObservations, unassignedTracks);
        AssignTier(AssociationTier.PredictedMotion, recognition, observations, result, unassignedObservations, unassignedTracks);
        return result;
    }

    private void AssignTier(
        AssociationTier tier,
        FrameRecognition recognition,
        IReadOnlyList<PlateObservation> observations,
        AssociationCandidate?[] assignments,
        HashSet<int> unassignedObservations,
        HashSet<Guid> unassignedTracks)
    {
        if (unassignedObservations.Count == 0 || unassignedTracks.Count == 0)
        {
            return;
        }

        var observationIndices = unassignedObservations.Order().ToArray();
        var tracks = _tracks.Where(track => unassignedTracks.Contains(track.Id)).ToArray();
        var candidates = new AssociationCandidate?[observationIndices.Length, tracks.Length];
        var scores = new float?[observationIndices.Length, tracks.Length];
        for (var row = 0; row < observationIndices.Length; row++)
        {
            var observation = observations[observationIndices[row]];
            for (var column = 0; column < tracks.Length; column++)
            {
                var candidate = CreateCandidate(tier, recognition, tracks[column], observation);
                candidates[row, column] = candidate;
                scores[row, column] = candidate?.Score;
            }
        }

        foreach (var (row, column) in MaximumWeightBipartiteMatcher.Match(scores))
        {
            var candidate = candidates[row, column]!;
            var observationIndex = observationIndices[row];
            assignments[observationIndex] = candidate;
            unassignedObservations.Remove(observationIndex);
            unassignedTracks.Remove(candidate.Track.Id);
        }
    }

    private AssociationCandidate? CreateCandidate(
        AssociationTier tier,
        FrameRecognition recognition,
        Track track,
        PlateObservation observation)
    {
        var elapsed = observation.CapturedAt - track.LastSeenAt;
        if (elapsed < TimeSpan.Zero || elapsed > _configuration.Tracking_TrackTimeout)
        {
            return null;
        }

        var predicted = track.Predict(
            observation.CapturedAt,
            recognition.SourceWidth,
            recognition.SourceHeight);
        var bounds = observation.Detection.Bounds;
        var lastIntersectionOverUnion = track.LastBounds.IntersectionOverUnion(bounds);
        var predictedIntersectionOverUnion = predicted.IntersectionOverUnion(bounds);
        var frameCenterDistance = CenterDistanceFraction(
            predicted,
            bounds,
            recognition.SourceWidth,
            recognition.SourceHeight);
        var predictedCenterDistance = CenterDistanceInPlateWidths(predicted, bounds);
        var scaleRatio = ScaleRatio(predicted, bounds);
        var observationText = PlateText.Normalize(observation.Read.Text);
        var stableText = track.StableText;
        var textEditDistance = PlateText.EditDistance(stableText, observationText);

        float distanceLimit;
        PlateAssociationKind kind;
        bool eligible;
        switch (tier)
        {
            case AssociationTier.ExactText:
                distanceLimit = _configuration.Tracking_MaximumExactTextCenterDistanceFraction;
                eligible = observationText.Length > 0
                    && string.Equals(stableText, observationText, StringComparison.Ordinal)
                    && frameCenterDistance <= distanceLimit
                    && scaleRatio <= _configuration.Tracking_MaximumExactOrSimilarScaleRatio;
                kind = PlateAssociationKind.ExactText;
                break;
            case AssociationTier.SimilarText:
                distanceLimit = _configuration.Tracking_MaximumSimilarTextCenterDistanceFraction;
                eligible = stableText.Length > 0
                    && observationText.Length > 0
                    && textEditDistance == 1
                    && frameCenterDistance <= distanceLimit
                    && scaleRatio <= _configuration.Tracking_MaximumExactOrSimilarScaleRatio;
                kind = PlateAssociationKind.SimilarText;
                break;
            case AssociationTier.PredictedMotion:
                distanceLimit = _configuration.Tracking_MaximumPredictedCenterDistanceInPlateWidths;
                eligible = TextSupportsMotion(stableText, observationText, textEditDistance)
                    && scaleRatio <= _configuration.Tracking_MaximumMotionScaleRatio
                    && (lastIntersectionOverUnion >= _configuration.Tracking_MinimumPreviousIntersectionOverUnion
                        || predictedIntersectionOverUnion >= _configuration.Tracking_MinimumPredictedIntersectionOverUnion
                        || predictedCenterDistance <= distanceLimit);
                kind = PlateAssociationKind.PredictedMotion;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tier));
        }

        if (!eligible)
        {
            return null;
        }

        var distance = tier == AssociationTier.PredictedMotion
            ? predictedCenterDistance
            : frameCenterDistance;
        var score = Score(
            distance,
            distanceLimit,
            scaleRatio,
            tier == AssociationTier.PredictedMotion
                ? _configuration.Tracking_MaximumMotionScaleRatio
                : _configuration.Tracking_MaximumExactOrSimilarScaleRatio,
            Math.Max(lastIntersectionOverUnion, predictedIntersectionOverUnion));
        return new AssociationCandidate(
            track,
            kind,
            score,
            predicted,
            lastIntersectionOverUnion,
            predictedIntersectionOverUnion,
            frameCenterDistance,
            scaleRatio,
            textEditDistance);
    }

    private bool TextSupportsMotion(string stableText, string observationText, int editDistance)
    {
        if (stableText.Length == 0 || observationText.Length == 0)
        {
            return false;
        }

        if (editDistance <= 1)
        {
            return true;
        }

        var shorter = stableText.Length <= observationText.Length ? stableText : observationText;
        var longer = stableText.Length <= observationText.Length ? observationText : stableText;
        return shorter.Length >= _configuration.Tracking_MinimumPartialTextLength
            && (longer.StartsWith(shorter, StringComparison.Ordinal)
                || longer.EndsWith(shorter, StringComparison.Ordinal));
    }

    private float Score(
        float distance,
        float distanceLimit,
        float scaleRatio,
        float maximumScaleRatio,
        float intersectionOverUnion)
    {
        var distanceScore = 1 - Math.Clamp(distance / Math.Max(0.0001f, distanceLimit), 0, 1);
        var scaleScore = maximumScaleRatio <= 1
            ? 1
            : 1 - Math.Clamp(
                MathF.Log(Math.Max(1, scaleRatio)) / MathF.Log(maximumScaleRatio),
                0,
                1);
        return _configuration.AssociationScore_DistanceWeight * distanceScore
            + _configuration.AssociationScore_ScaleWeight * scaleScore
            + _configuration.AssociationScore_OverlapWeight * Math.Clamp(intersectionOverUnion, 0, 1);
    }

    private static float CenterDistanceFraction(
        BoundingBox left,
        BoundingBox right,
        int frameWidth,
        int frameHeight)
    {
        var diagonal = MathF.Sqrt((float)frameWidth * frameWidth + (float)frameHeight * frameHeight);
        if (diagonal <= 0)
        {
            diagonal = MathF.Max(
                1,
                MathF.Sqrt(
                    MathF.Max(left.Right, right.Right) * MathF.Max(left.Right, right.Right)
                    + MathF.Max(left.Bottom, right.Bottom) * MathF.Max(left.Bottom, right.Bottom)));
        }

        return CenterDistance(left, right) / diagonal;
    }

    private static float CenterDistanceInPlateWidths(BoundingBox left, BoundingBox right)
    {
        var meanWidth = MathF.Max(1, (left.Width + right.Width) / 2);
        return CenterDistance(left, right) / meanWidth;
    }

    private static float CenterDistance(BoundingBox left, BoundingBox right)
    {
        var horizontal = (left.Left + left.Right - right.Left - right.Right) / 2;
        var vertical = (left.Top + left.Bottom - right.Top - right.Bottom) / 2;
        return MathF.Sqrt(horizontal * horizontal + vertical * vertical);
    }

    private static float ScaleRatio(BoundingBox left, BoundingBox right)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            return float.PositiveInfinity;
        }

        return MathF.Max(
            MathF.Max(left.Width / right.Width, right.Width / left.Width),
            MathF.Max(left.Height / right.Height, right.Height / left.Height));
    }

    private void Expire(DateTimeOffset now) =>
        _tracks.RemoveAll(track => now - track.LastSeenAt > _configuration.Tracking_TrackTimeout);

    private enum AssociationTier
    {
        ExactText,
        SimilarText,
        PredictedMotion
    }

    private sealed record AssociationCandidate(
        Track Track,
        PlateAssociationKind Kind,
        float Score,
        BoundingBox PredictedBounds,
        float LastIntersectionOverUnion,
        float PredictedIntersectionOverUnion,
        float FrameCenterDistance,
        float ScaleRatio,
        int TextEditDistance);

    private sealed class Track(
        Guid id,
        DateTimeOffset firstSeenAt,
        BoundingBox initialBounds,
        RecognitionTuningConfiguration configuration)
    {
        public Guid Id { get; } = id;
        public DateTimeOffset FirstSeenAt { get; } = firstSeenAt;
        public DateTimeOffset LastSeenAt { get; private set; } = firstSeenAt;
        public BoundingBox LastBounds { get; private set; } = initialBounds;
        public ConsensusResult? ConfirmedConsensus { get; private set; }
        public int Revision { get; private set; }
        public List<PlateObservation> Observations { get; } = [];
        public string StableText => PlateEvidence.StableText(Observations, configuration);

        public bool ShouldPublish(ConsensusResult result) =>
            ConfirmedConsensus is null
            || (!string.Equals(
                    result.NormalizedPlate,
                    ConfirmedConsensus.NormalizedPlate,
                    StringComparison.Ordinal)
                && result.ObservationCount >= ConfirmedConsensus.ObservationCount
                    + configuration.ConfirmationCorrection_MinimumAdditionalObservations
                && result.Confidence >= configuration.ConfirmationCorrection_MinimumConfidence);

        public void Publish(ConsensusResult result)
        {
            if (ConfirmedConsensus is not null)
            {
                Revision++;
            }

            ConfirmedConsensus = result;
        }

        public void Add(PlateObservation observation, int maximumObservations)
        {
            LastSeenAt = observation.CapturedAt;
            LastBounds = observation.Detection.Bounds;
            Observations.Add(observation);
            if (Observations.Count > maximumObservations)
            {
                Observations.RemoveRange(0, Observations.Count - maximumObservations);
            }
        }

        public BoundingBox Predict(
            DateTimeOffset capturedAt,
            int frameWidth,
            int frameHeight)
        {
            if (Observations.Count < 2)
            {
                return LastBounds;
            }

            var previous = Observations[^2];
            var latest = Observations[^1];
            var observationInterval = (latest.CapturedAt - previous.CapturedAt).TotalSeconds;
            var predictionInterval = (capturedAt - latest.CapturedAt).TotalSeconds;
            if (observationInterval <= 0 || predictionInterval <= 0)
            {
                return LastBounds;
            }

            var steps = (float)Math.Clamp(
                predictionInterval / observationInterval,
                0,
                configuration.Tracking_MaximumPredictionSteps);
            var previousBounds = previous.Detection.Bounds;
            var latestBounds = latest.Detection.Bounds;
            var previousCenterX = (previousBounds.Left + previousBounds.Right) / 2;
            var previousCenterY = (previousBounds.Top + previousBounds.Bottom) / 2;
            var latestCenterX = (latestBounds.Left + latestBounds.Right) / 2;
            var latestCenterY = (latestBounds.Top + latestBounds.Bottom) / 2;
            var centerX = latestCenterX + (latestCenterX - previousCenterX) * steps;
            var centerY = latestCenterY + (latestCenterY - previousCenterY) * steps;
            var width = Math.Clamp(
                latestBounds.Width + (latestBounds.Width - previousBounds.Width) * steps,
                latestBounds.Width * configuration.Tracking_PredictionMinimumScale,
                latestBounds.Width * configuration.Tracking_PredictionMaximumScale);
            var height = Math.Clamp(
                latestBounds.Height + (latestBounds.Height - previousBounds.Height) * steps,
                latestBounds.Height * configuration.Tracking_PredictionMinimumScale,
                latestBounds.Height * configuration.Tracking_PredictionMaximumScale);
            var predicted = new BoundingBox(
                centerX - width / 2,
                centerY - height / 2,
                centerX + width / 2,
                centerY + height / 2);
            return frameWidth > 0 && frameHeight > 0
                ? predicted.Clamp(frameWidth, frameHeight)
                : predicted;
        }

        public PlateTrackSnapshot ToSnapshot()
        {
            var latest = Observations[^1];
            return new PlateTrackSnapshot(
                Id,
                FirstSeenAt,
                LastSeenAt,
                LastBounds,
                Observations.Count,
                ConfirmedConsensus is not null,
                latest.FrameSequence,
                latest.Read.Text,
                latest.Detection.Confidence,
                latest.Read.Confidence,
                latest.Quality);
        }
    }
}
