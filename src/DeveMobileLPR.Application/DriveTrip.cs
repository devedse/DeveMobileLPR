using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application;

/// <summary>
/// Everything whose lifetime is a single drive: the trip row it writes to, the plates seen during
/// it, and the location tracker feeding it. <see cref="DriveCoordinator"/> creates one when a drive
/// starts and disposes it when the drive ends, so state cannot survive into the next drive. That
/// matters more than it sounds: the alternative is a list of fields to clear on every start, and a
/// field that is forgotten stamps the next trip with the previous trip's data.
/// </summary>
/// <remarks>
/// <see cref="TripId"/>, <see cref="StartedAt"/> and <see cref="Location"/> are immutable and safe
/// to read from any thread. Everything else must only be touched while holding the coordinator's
/// state gate, because the recognition worker and the UI both reach it.
/// </remarks>
internal sealed class DriveTrip(long tripId, DateTimeOffset startedAt, IDriveLocationTracker location)
    : IDisposable
{
    /// <summary>
    /// A fix older than this counts as no fix. At 50 km/h a twenty-second-old position is already
    /// some 275 m behind, which is enough to put a sighting on the wrong street.
    /// </summary>
    public static readonly TimeSpan MaximumLocationAge = TimeSpan.FromSeconds(20);

    private const int RecentSightingLimit = 5;

    public long TripId { get; } = tripId;
    public DateTimeOffset StartedAt { get; } = startedAt;
    public IDriveLocationTracker Location { get; } = location;

    public ConfirmedOverlayTracker ConfirmedPlates { get; } = new(() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, IReadOnlyList<DriveOverlay>> _liveOverlaysBySource = new(StringComparer.Ordinal);
    public IReadOnlyList<DriveOverlay> LiveOverlays => _liveOverlaysBySource.Values.SelectMany(static value => value).ToArray();
    public Sighting? MostExpensive { get; private set; }
    public int UniqueVehicleCount => _uniqueVehicles.Count;

    private readonly Dictionary<long, Sighting> _sightings = [];
    private readonly List<Sighting> _recentSightings = [];
    private readonly HashSet<string> _uniqueVehicles = new(StringComparer.Ordinal);

    /// <summary>
    /// The current position, or null when the tracker has nothing recent enough to trust. Returning
    /// null leaves a sighting unlocated, which is the honest answer — a stale coordinate would
    /// silently place the car somewhere it has never been.
    /// </summary>
    public GeoPoint? LocationAt(DateTimeOffset now)
    {
        if (Location.Latest is not { } fix)
        {
            return null;
        }

        // Duration() also rejects a fix stamped in the future, which a skewed device clock produces.
        return (now - fix.ObservedAt).Duration() <= MaximumLocationAge ? fix.Point : null;
    }

    /// <summary>Records a confirmation, replacing any earlier revision of the same sighting.</summary>
    public void AddOrReplaceSighting(Sighting sighting)
    {
        _sightings[sighting.Id] = sighting;
        _uniqueVehicles.Clear();
        _uniqueVehicles.UnionWith(_sightings.Values.Select(static item => item.NormalizedPlate));
        _recentSightings.RemoveAll(item => item.Id == sighting.Id);
        _recentSightings.Insert(0, sighting);
        if (_recentSightings.Count > RecentSightingLimit)
        {
            _recentSightings.RemoveRange(RecentSightingLimit, _recentSightings.Count - RecentSightingLimit);
        }
        MostExpensive = _sightings.Values
            .Where(static item => item.Vehicle?.CatalogPrice is not null)
            .OrderByDescending(static item => item.Vehicle!.CatalogPrice)
            .FirstOrDefault();
    }

    public IReadOnlyList<Sighting> RecentSightings() => _recentSightings.ToArray();

    public void SetLiveOverlays(string sourceId, IReadOnlyList<DriveOverlay> overlays) =>
        _liveOverlaysBySource[sourceId] = overlays;

    /// <summary>Clears what is drawn without ending the trip, for a mid-drive input change.</summary>
    public void ClearOverlays()
    {
        _liveOverlaysBySource.Clear();
        ConfirmedPlates.Clear();
    }

    public void Dispose()
    {
        Location.Stop();
        Location.Dispose();
    }
}
