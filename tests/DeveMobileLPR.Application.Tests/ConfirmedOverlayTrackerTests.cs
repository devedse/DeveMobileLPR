using DeveMobileLPR.Geometry;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application.Tests;

public sealed class ConfirmedOverlayTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly BoundingBox PlateBounds = new(10, 20, 40, 30);

    [Fact]
    public void HighlightGivesWayToTheConfirmedKindWhenTheWindowPasses()
    {
        var clock = new FakeClock(Start);
        var tracker = new ConfirmedOverlayTracker(clock.Now);
        tracker.ObserveFrame(1920, 1080, []);
        tracker.Confirm(Confirmation(bounds: PlateBounds), PlateSighting("AB1234"), PriorVehicleSightings.None);

        Assert.Equal(DriveOverlayKind.ConfirmedHighlight, tracker.CreateOverlays().Single().Kind);

        clock.Advance(ConfirmedOverlayTracker.HighlightWindow + TimeSpan.FromMilliseconds(1));

        Assert.Equal(DriveOverlayKind.Confirmed, tracker.CreateOverlays().Single().Kind);
    }

    [Fact]
    public void VehiclesSeenOnEarlierTripsBecomeKnownAfterTheHighlightWindow()
    {
        var clock = new FakeClock(Start);
        var tracker = new ConfirmedOverlayTracker(clock.Now);
        tracker.ObserveFrame(1920, 1080, []);
        tracker.Confirm(
            Confirmation(bounds: PlateBounds),
            PlateSighting("AB1234"),
            new PriorVehicleSightings(3, Start.AddDays(-2)));

        clock.Advance(ConfirmedOverlayTracker.HighlightWindow + TimeSpan.FromMilliseconds(1));

        var overlay = tracker.CreateOverlays().Single();
        Assert.Equal(DriveOverlayKind.ConfirmedKnown, overlay.Kind);
        Assert.Equal("3× · 2d", overlay.Detail);
    }

    [Fact]
    public void PlateLeavesTheScreenWhenTheLingerWindowPassesWithoutFurtherFrames()
    {
        var clock = new FakeClock(Start);
        var tracker = new ConfirmedOverlayTracker(clock.Now);
        tracker.ObserveFrame(1920, 1080, []);
        tracker.Confirm(Confirmation(bounds: PlateBounds), PlateSighting("AB1234"), PriorVehicleSightings.None);

        clock.Advance(ConfirmedOverlayTracker.LingerWindow - TimeSpan.FromSeconds(1));
        Assert.Single(tracker.CreateOverlays());

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(tracker.CreateOverlays());
    }

    [Fact]
    public void LiveTrackKeepsRefreshingTheLingerWindowAndBounds()
    {
        var clock = new FakeClock(Start);
        var tracker = new ConfirmedOverlayTracker(clock.Now);
        var trackId = Guid.NewGuid();
        tracker.ObserveFrame(1920, 1080, []);
        tracker.Confirm(Confirmation(trackId, PlateBounds), PlateSighting("AB1234"), PriorVehicleSightings.None);

        var moved = new BoundingBox(60, 20, 90, 30);
        clock.Advance(ConfirmedOverlayTracker.LingerWindow - TimeSpan.FromSeconds(1));
        tracker.ObserveFrame(1920, 1080, [Track(trackId, moved, confirmed: true)]);
        clock.Advance(ConfirmedOverlayTracker.LingerWindow - TimeSpan.FromSeconds(1));

        var overlay = Assert.Single(tracker.CreateOverlays());
        Assert.Equal(moved, overlay.Bounds);
    }

    [Fact]
    public void CorrectionDoesNotRestartTheHighlight()
    {
        var clock = new FakeClock(Start);
        var tracker = new ConfirmedOverlayTracker(clock.Now);
        var trackId = Guid.NewGuid();
        tracker.ObserveFrame(1920, 1080, []);
        tracker.Confirm(Confirmation(trackId, PlateBounds), PlateSighting("AB12BE"), PriorVehicleSightings.None);

        clock.Advance(ConfirmedOverlayTracker.HighlightWindow + TimeSpan.FromMilliseconds(1));
        tracker.Confirm(
            Confirmation(trackId, PlateBounds, revision: 1),
            PlateSighting("AB12BG"),
            PriorVehicleSightings.None);

        var overlay = Assert.Single(tracker.CreateOverlays());
        Assert.Equal(DriveOverlayKind.Confirmed, overlay.Kind);
        Assert.Equal("AB-12-BG", overlay.Title);
    }

    [Fact]
    public void EveryConfirmedPlateStaysOnScreen()
    {
        var clock = new FakeClock(Start);
        var tracker = new ConfirmedOverlayTracker(clock.Now);
        tracker.ObserveFrame(1920, 1080, []);
        tracker.Confirm(Confirmation(bounds: PlateBounds), PlateSighting("AB1234"), PriorVehicleSightings.None);
        tracker.Confirm(
            Confirmation(bounds: new BoundingBox(200, 20, 230, 30)),
            PlateSighting("CD5678"),
            PriorVehicleSightings.None);

        Assert.Equal(2, tracker.CreateOverlays().Count);
    }

    [Fact]
    public void OldestExpiringPlateIsDroppedBeyondTheTrackedLimit()
    {
        var clock = new FakeClock(Start);
        var tracker = new ConfirmedOverlayTracker(clock.Now);
        tracker.ObserveFrame(1920, 1080, []);
        for (var index = 0; index <= ConfirmedOverlayTracker.MaxTrackedPlates; index++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            tracker.Confirm(
                Confirmation(bounds: new BoundingBox(index * 40, 20, index * 40 + 30, 30)),
                PlateSighting($"PL{index:0000}"),
                PriorVehicleSightings.None);
        }

        var titles = tracker.CreateOverlays().Select(static overlay => overlay.Title).ToArray();
        Assert.Equal(ConfirmedOverlayTracker.MaxTrackedPlates, titles.Length);
        Assert.DoesNotContain("PL0000", titles);
    }

    [Fact]
    public void ReadingOverlappingAConfirmedPlateIsSuppressed()
    {
        var clock = new FakeClock(Start);
        var tracker = new ConfirmedOverlayTracker(clock.Now);
        tracker.ObserveFrame(1920, 1080, []);
        tracker.Confirm(Confirmation(bounds: PlateBounds), PlateSighting("AB1234"), PriorVehicleSightings.None);

        Assert.True(tracker.Suppresses(new BoundingBox(11, 21, 41, 31)));
        Assert.False(tracker.Suppresses(new BoundingBox(500, 20, 530, 30)));
    }

    [Fact]
    public void ExpiredPlateNoLongerSuppressesLiveReadings()
    {
        var clock = new FakeClock(Start);
        var tracker = new ConfirmedOverlayTracker(clock.Now);
        tracker.ObserveFrame(1920, 1080, []);
        tracker.Confirm(Confirmation(bounds: PlateBounds), PlateSighting("AB1234"), PriorVehicleSightings.None);

        clock.Advance(ConfirmedOverlayTracker.LingerWindow + TimeSpan.FromSeconds(1));

        Assert.False(tracker.Suppresses(PlateBounds));
    }

    [Fact]
    public void ResolutionChangeDiscardsPlatesProjectedAgainstTheOldGeometry()
    {
        var clock = new FakeClock(Start);
        var tracker = new ConfirmedOverlayTracker(clock.Now);
        tracker.ObserveFrame(1920, 1080, []);
        tracker.Confirm(Confirmation(bounds: PlateBounds), PlateSighting("AB1234"), PriorVehicleSightings.None);
        Assert.Single(tracker.CreateOverlays());

        tracker.ObserveFrame(1280, 720, []);

        Assert.Empty(tracker.CreateOverlays());
    }

    [Fact]
    public void OverlaysCarryTheGeometryOfTheFrameTheyWereObservedIn()
    {
        var clock = new FakeClock(Start);
        var tracker = new ConfirmedOverlayTracker(clock.Now);
        tracker.ObserveFrame(1920, 1080, []);
        tracker.Confirm(Confirmation(bounds: PlateBounds), PlateSighting("AB1234"), PriorVehicleSightings.None);

        var overlay = Assert.Single(tracker.CreateOverlays());
        Assert.Equal(1920, overlay.SourceWidth);
        Assert.Equal(1080, overlay.SourceHeight);
    }

    private static ConfirmedPlate Confirmation(Guid? trackId = null, BoundingBox bounds = default, int revision = 0) =>
        new(
            trackId ?? Guid.NewGuid(),
            Start,
            Start,
            bounds,
            new ConsensusResult("AB1234", "AB-12-34", null, 0.9f, 3))
        {
            Revision = revision
        };

    private static Sighting PlateSighting(string normalizedPlate) => new(
        1,
        normalizedPlate,
        PlateText.Normalize(normalizedPlate).Length == 6
            ? PlateText.FormatDutchPlate(PlateText.Normalize(normalizedPlate))
            : normalizedPlate,
        null,
        Start,
        Start,
        0.9f,
        3,
        null,
        null);

    private static PlateTrackSnapshot Track(Guid trackId, BoundingBox bounds, bool confirmed) => new(
        trackId,
        Start,
        Start,
        bounds,
        3,
        confirmed,
        1,
        "AB1234",
        0.9f,
        0.9f,
        0.9f);

    private sealed class FakeClock(DateTimeOffset start)
    {
        private DateTimeOffset _now = start;

        public DateTimeOffset Now() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
