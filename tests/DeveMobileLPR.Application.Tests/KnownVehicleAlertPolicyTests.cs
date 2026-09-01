using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application.Tests;

public sealed class KnownVehicleAlertPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(KnownVehicleSoundMode.Off, false)]
    [InlineData(KnownVehicleSoundMode.Always, true)]
    public void BasicModesRespectKnownVehicleState(KnownVehicleSoundMode mode, bool expected)
    {
        var prior = new PriorVehicleSightings(2, Now.AddDays(-1), new GeoPoint(52, 5, 10));

        Assert.Equal(expected, KnownVehicleAlertPolicy.ShouldPlay(mode, prior, SightingAt(Now, 52, 5)));
        Assert.False(KnownVehicleAlertPolicy.ShouldPlay(mode, PriorVehicleSightings.None, SightingAt(Now, 52, 5)));
    }

    [Fact]
    public void DifferentLocationRequiresBothLocationsAndMoreThan100Meters()
    {
        var nearby = new PriorVehicleSightings(1, Now.AddDays(-1), new GeoPoint(52, 5, 10));
        var farAway = new PriorVehicleSightings(1, Now.AddDays(-1), new GeoPoint(52, 5, 10));

        Assert.False(KnownVehicleAlertPolicy.ShouldPlay(
            KnownVehicleSoundMode.DifferentLocation,
            nearby,
            SightingAt(Now, 52.0005, 5)));
        Assert.True(KnownVehicleAlertPolicy.ShouldPlay(
            KnownVehicleSoundMode.DifferentLocation,
            farAway,
            SightingAt(Now, 52.002, 5)));
        Assert.False(KnownVehicleAlertPolicy.ShouldPlay(
            KnownVehicleSoundMode.DifferentLocation,
            nearby with { LastLocation = null },
            SightingAt(Now, 52.002, 5)));
        Assert.False(KnownVehicleAlertPolicy.ShouldPlay(
            KnownVehicleSoundMode.DifferentLocation,
            nearby,
            SightingAt(Now, null, null)));
    }

    [Theory]
    [InlineData(23, false)]
    [InlineData(24, true)]
    [InlineData(48, true)]
    public void After24HoursUsesThePreviousSightingTime(int hours, bool expected)
    {
        var prior = new PriorVehicleSightings(1, Now.AddHours(-hours));

        Assert.Equal(expected, KnownVehicleAlertPolicy.ShouldPlay(
            KnownVehicleSoundMode.After24Hours,
            prior,
            SightingAt(Now, 52, 5)));
    }

    private static Sighting SightingAt(DateTimeOffset seenAt, double? latitude, double? longitude) => new(
        1,
        "AB1234",
        "AB-12-34",
        "NL",
        seenAt,
        seenAt,
        0.95f,
        3,
        latitude is { } lat && longitude is { } lon ? new GeoPoint(lat, lon, 10) : null,
        null);
}
