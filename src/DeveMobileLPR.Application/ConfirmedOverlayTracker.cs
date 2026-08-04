using DeveMobileLPR.Geometry;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application;

/// <summary>
/// Keeps confirmed plates on screen after the detector stops reporting them, so a plate that
/// has already passed out of frame stays readable for a few seconds. Plates are keyed by track,
/// which the recognition stream resets whenever the source geometry changes, so a resolution
/// change discards every tracked plate rather than projecting it against stale dimensions.
/// This type is not thread safe; the caller owns synchronisation.
/// </summary>
internal sealed class ConfirmedOverlayTracker(Func<DateTimeOffset> clock)
{
    /// <summary>How long a freshly confirmed plate is drawn in the highlight colour.</summary>
    public static readonly TimeSpan HighlightWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a plate stays on screen after the detector last reported its track. This is only
    /// the tail: while a track is still being reported, <see cref="ObserveFrame"/> keeps pushing the
    /// window out, so a car that stays in view keeps its overlay for as long as it is tracked.
    /// </summary>
    public static readonly TimeSpan LingerWindow = TimeSpan.FromSeconds(4);

    /// <summary>Upper bound on simultaneously drawn plates; the soonest to expire is dropped first.</summary>
    public const int MaxTrackedPlates = 8;

    /// <summary>
    /// Overlap at which a live reading is considered to be the same plate as a confirmed one.
    /// The confirmed overlay carries strictly more information, so the reading is dropped.
    /// </summary>
    public const float SuppressionIntersectionOverUnion = 0.35f;

    private readonly Dictionary<Guid, TrackedPlate> _plates = [];
    private int _sourceWidth = 1;
    private int _sourceHeight = 1;

    public void Clear()
    {
        _plates.Clear();
        _sourceWidth = 1;
        _sourceHeight = 1;
    }

    /// <summary>
    /// Refreshes the bounds and linger window of plates whose tracks are still alive, and drops
    /// plates whose linger window has passed.
    /// </summary>
    public void ObserveFrame(int sourceWidth, int sourceHeight, IReadOnlyList<PlateTrackSnapshot> tracks)
    {
        if (sourceWidth != _sourceWidth || sourceHeight != _sourceHeight)
        {
            _plates.Clear();
            _sourceWidth = Math.Max(1, sourceWidth);
            _sourceHeight = Math.Max(1, sourceHeight);
        }

        var now = clock();
        foreach (var track in tracks)
        {
            if (track.Confirmed && _plates.TryGetValue(track.TrackId, out var plate))
            {
                _plates[track.TrackId] = plate with { Bounds = track.Bounds, ExpiresAt = now + LingerWindow };
            }
        }

        foreach (var expired in _plates
            .Where(pair => pair.Value.ExpiresAt <= now)
            .Select(static pair => pair.Key)
            .ToArray())
        {
            _plates.Remove(expired);
        }
    }

    /// <summary>
    /// Records a confirmation. A corrected plate keeps the highlight window of the original
    /// confirmation so a late correction does not flash the overlay a second time.
    /// </summary>
    public void Confirm(ConfirmedPlate confirmation, Sighting sighting, PriorVehicleSightings prior)
    {
        var now = clock();
        var highlightUntil = confirmation.Revision == 0
            ? now + HighlightWindow
            : _plates.TryGetValue(confirmation.TrackId, out var existing) ? existing.HighlightUntil : now;
        _plates[confirmation.TrackId] = new TrackedPlate(
            sighting,
            prior,
            confirmation.LastBounds,
            highlightUntil,
            now + LingerWindow);
        while (_plates.Count > MaxTrackedPlates)
        {
            _plates.Remove(_plates.OrderBy(static pair => pair.Value.ExpiresAt).First().Key);
        }
    }

    /// <summary>True when a live reading covers a plate that is already confirmed.</summary>
    public bool Suppresses(BoundingBox bounds) => ActivePlates(clock()).Any(plate =>
        bounds.IntersectionOverUnion(plate.Bounds) >= SuppressionIntersectionOverUnion);

    public IReadOnlyList<DriveOverlay> CreateOverlays()
    {
        var now = clock();
        return ActivePlates(now)
            .Select(plate => new DriveOverlay(
                plate.Bounds,
                _sourceWidth,
                _sourceHeight,
                plate.Sighting.DisplayPlate,
                FormatDetail(plate, now),
                plate.Sighting.Confidence,
                ResolveKind(plate, now)))
            .ToArray();
    }

    /// <summary>
    /// Expiry is applied when reading rather than only when a frame arrives, so plates still leave
    /// the screen on time if the recognition stream stalls.
    /// </summary>
    private IEnumerable<TrackedPlate> ActivePlates(DateTimeOffset now) =>
        _plates.Values.Where(plate => plate.ExpiresAt > now);

    private static DriveOverlayKind ResolveKind(TrackedPlate plate, DateTimeOffset now) => plate.HighlightUntil > now
        ? DriveOverlayKind.ConfirmedHighlight
        : plate.Prior.SightingCount > 0
            ? DriveOverlayKind.ConfirmedKnown
            : DriveOverlayKind.Confirmed;

    private static string FormatDetail(TrackedPlate plate, DateTimeOffset now)
    {
        var vehicle = plate.Sighting.Vehicle;
        var brand = string.Join(' ', new[] { vehicle?.Make, vehicle?.Model }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (plate.Prior is { SightingCount: > 0, LastSeenAt: { } lastSeen })
        {
            var history = $"{plate.Prior.SightingCount}× · {FormatLastSeen(lastSeen, now)}";
            return string.IsNullOrWhiteSpace(brand) ? history : $"{brand} · {history}";
        }

        return vehicle is null
            ? "no RDW details"
            : string.Join(' ', new[] { brand, CompactPrice(vehicle.CatalogPrice), vehicle.BodyType }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string FormatLastSeen(DateTimeOffset value, DateTimeOffset now)
    {
        var days = (int)(now - value).TotalDays;
        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 14 => $"{days}d",
            < 56 => $"{days / 7}w",
            < 365 => $"{days / 30}mo",
            _ => $"{days / 365}y"
        };
    }

    private static string CompactPrice(decimal? value) => value switch
    {
        null => "—",
        >= 1_000_000 => $"€{value.Value / 1_000_000:0.#}m",
        >= 1_000 => $"€{value.Value / 1_000:0}k",
        _ => $"€{value.Value:0}"
    };

    private sealed record TrackedPlate(
        Sighting Sighting,
        PriorVehicleSightings Prior,
        BoundingBox Bounds,
        DateTimeOffset HighlightUntil,
        DateTimeOffset ExpiresAt);
}
