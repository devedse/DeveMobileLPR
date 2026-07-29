using DeveMobileLPR.Geometry;

namespace DeveMobileLPR.Recognition;

public sealed record TrackingOptions(
    float MinimumIntersectionOverUnion = 0.18f,
    TimeSpan? TrackTimeout = null,
    int MaximumObservationsPerTrack = 12)
{
    public TimeSpan EffectiveTrackTimeout => TrackTimeout ?? TimeSpan.FromSeconds(1.5);
}

public sealed class PlateTrackManager
{
    private readonly TrackingOptions _options;
    private readonly TemporalConsensus _consensus;
    private readonly List<Track> _tracks = [];

    public PlateTrackManager(TrackingOptions? options = null, ConsensusOptions? consensusOptions = null)
    {
        _options = options ?? new TrackingOptions();
        _consensus = new TemporalConsensus(consensusOptions);
    }

    public IReadOnlyList<ConfirmedPlate> Update(FrameRecognition recognition)
        => UpdateDetailed(recognition).Confirmations;

    public PlateTrackingUpdate UpdateDetailed(FrameRecognition recognition)
    {
        Expire(recognition.CapturedAt);
        var confirmations = new List<ConfirmedPlate>();
        var associations = new List<PlateTrackAssociation>(recognition.Observations.Count);
        var assignedTracks = new HashSet<Guid>();

        foreach (var observation in recognition.Observations.OrderByDescending(static item => item.Detection.Confidence))
        {
            var match = _tracks
                .Where(candidate => !assignedTracks.Contains(candidate.Id))
                .Select(candidate => new
                {
                    Track = candidate,
                    Score = candidate.LastBounds.IntersectionOverUnion(observation.Detection.Bounds)
                })
                .Where(candidate => candidate.Score >= _options.MinimumIntersectionOverUnion)
                .OrderByDescending(static candidate => candidate.Score)
                .FirstOrDefault();
            var track = match?.Track;
            var created = track is null;

            if (track is null)
            {
                track = new Track(Guid.NewGuid(), observation.CapturedAt, observation.Detection.Bounds);
                _tracks.Add(track);
            }

            assignedTracks.Add(track.Id);
            track.Add(observation, _options.MaximumObservationsPerTrack);
            associations.Add(new PlateTrackAssociation(
                track.Id,
                recognition.FrameSequence,
                created,
                created ? null : match!.Score));
            if (!track.Confirmed)
            {
                var result = _consensus.Resolve(track.Observations);
                if (result is not null)
                {
                    track.Confirmed = true;
                    confirmations.Add(new ConfirmedPlate(
                        track.Id,
                        track.FirstSeenAt,
                        track.LastSeenAt,
                        track.LastBounds,
                        result));
                }
            }
        }

        return new PlateTrackingUpdate(
            confirmations,
            _tracks.Select(static track => track.ToSnapshot()).ToArray(),
            associations);
    }

    public void Reset() => _tracks.Clear();

    private void Expire(DateTimeOffset now) =>
        _tracks.RemoveAll(track => now - track.LastSeenAt > _options.EffectiveTrackTimeout);

    private sealed class Track(Guid id, DateTimeOffset firstSeenAt, BoundingBox initialBounds)
    {
        public Guid Id { get; } = id;
        public DateTimeOffset FirstSeenAt { get; } = firstSeenAt;
        public DateTimeOffset LastSeenAt { get; private set; } = firstSeenAt;
        public BoundingBox LastBounds { get; private set; } = initialBounds;
        public bool Confirmed { get; set; }
        public List<PlateObservation> Observations { get; } = [];

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

        public PlateTrackSnapshot ToSnapshot()
        {
            var latest = Observations[^1];
            return new PlateTrackSnapshot(
                Id,
                FirstSeenAt,
                LastSeenAt,
                LastBounds,
                Observations.Count,
                Confirmed,
                latest.FrameSequence,
                latest.Read.Text,
                latest.Detection.Confidence,
                latest.Read.Confidence,
                latest.Quality);
        }
    }
}
