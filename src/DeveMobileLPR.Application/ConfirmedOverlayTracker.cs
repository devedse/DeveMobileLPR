using DeveMobileLPR.Geometry;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application;

/// <summary>
/// Keeps confirmed plates on screen briefly after the detector stops reporting them, so a plate
/// does not vanish the instant its car leaves frame. Plates are keyed by track,
/// which the recognition stream resets whenever the source geometry changes, so a resolution
/// change discards every tracked plate rather than projecting it against stale dimensions.
/// This type is not thread safe; the caller owns synchronisation.
/// </summary>
internal sealed class ConfirmedOverlayTracker(Func<DateTimeOffset> clock)
{
    /// <summary>
    /// How long a plate stays on screen after the detector last reported its track. This is only
    /// the tail: while a track is still being reported, <see cref="ObserveFrame"/> keeps pushing the
    /// window out, so a car that stays in view keeps its overlay for as long as it is tracked.
    /// </summary>
    /// <remarks>
    /// This has to stay above the interval between analyzed frames. The window is only refreshed
    /// when a frame reports the track, so if one detection is missed and the next frame lands later
    /// than this window, the overlay drops and reappears — a visible blink rather than a clean fade.
    /// </remarks>
    public static readonly TimeSpan LingerWindow = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound on simultaneously drawn plates; the soonest to expire is dropped first.</summary>
    public const int MaxTrackedPlates = 8;

    /// <summary>
    /// Overlap at which a live reading is considered to be the same plate as a confirmed one.
    /// The confirmed overlay carries strictly more information, so the reading is dropped.
    /// </summary>
    public const float SuppressionIntersectionOverUnion = 0.35f;

    private readonly Dictionary<Guid, TrackedPlate> _plates = [];
    private readonly Dictionary<string, (int Width, int Height)> _sourceSizes = new(StringComparer.Ordinal);

    public void Clear()
    {
        _plates.Clear();
        _sourceSizes.Clear();
    }

    /// <summary>
    /// Refreshes the bounds and linger window of plates whose tracks are still alive, and drops
    /// plates whose linger window has passed.
    /// </summary>
    public void ObserveFrame(int sourceWidth, int sourceHeight, IReadOnlyList<PlateTrackSnapshot> tracks, string sourceId = "")
    {
        var size = (Width: Math.Max(1, sourceWidth), Height: Math.Max(1, sourceHeight));
        if (_sourceSizes.TryGetValue(sourceId, out var priorSize) && priorSize != size)
        {
            foreach (var stale in _plates.Where(pair => pair.Value.SourceId == sourceId).Select(pair => pair.Key).ToArray())
            {
                _plates.Remove(stale);
            }
        }
        _sourceSizes[sourceId] = size;

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
    /// Records a confirmation, replacing whatever the track previously resolved to. A correction
    /// therefore takes effect immediately, including its plate text and prior-sighting history.
    /// </summary>
    public void Confirm(ConfirmedPlate confirmation, Sighting sighting, PriorVehicleSightings prior, string sourceId = "")
    {
        var size = _sourceSizes.GetValueOrDefault(sourceId, (1, 1));
        _plates[confirmation.TrackId] = new TrackedPlate(
            sighting,
            prior,
            confirmation.LastBounds,
            clock() + LingerWindow,
            sourceId,
            size.Item1,
            size.Item2);
        while (_plates.Count > MaxTrackedPlates)
        {
            _plates.Remove(_plates.OrderBy(static pair => pair.Value.ExpiresAt).First().Key);
        }
    }

    /// <summary>True when a live reading covers a plate that is already confirmed.</summary>
    public bool Suppresses(BoundingBox bounds, string sourceId = "") => ActivePlates(clock()).Any(plate =>
        plate.SourceId == sourceId
        && bounds.IntersectionOverUnion(plate.Bounds) >= SuppressionIntersectionOverUnion);

    public IReadOnlyList<DriveOverlay> CreateOverlays()
    {
        var now = clock();
        return ActivePlates(now)
            .Select(plate => new DriveOverlay(
                plate.Bounds,
                plate.SourceWidth,
                plate.SourceHeight,
                plate.Sighting.DisplayPlate,
                FormatDetail(plate, now),
                plate.Sighting.Confidence,
                ResolveKind(plate)) { SourceId = plate.SourceId })
            .ToArray();
    }

    /// <summary>
    /// Expiry is applied when reading rather than only when a frame arrives, so plates still leave
    /// the screen on time if the recognition stream stalls.
    /// </summary>
    private IEnumerable<TrackedPlate> ActivePlates(DateTimeOffset now) =>
        _plates.Values.Where(plate => plate.ExpiresAt > now);

    private static DriveOverlayKind ResolveKind(TrackedPlate plate) => plate.Prior.SightingCount > 0
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
        DateTimeOffset ExpiresAt,
        string SourceId,
        int SourceWidth,
        int SourceHeight);
}
