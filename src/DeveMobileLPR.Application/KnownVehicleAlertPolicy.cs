using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application;

internal static class KnownVehicleAlertPolicy
{
    internal const double DifferentLocationThresholdMeters = 100;
    internal static readonly TimeSpan NotSeenRecentlyThreshold = TimeSpan.FromHours(24);

    public static bool ShouldPlay(
        KnownVehicleSoundMode mode,
        PriorVehicleSightings prior,
        Sighting current)
    {
        if (prior.SightingCount <= 0)
        {
            return false;
        }

        return mode switch
        {
            KnownVehicleSoundMode.Off => false,
            KnownVehicleSoundMode.Always => true,
            KnownVehicleSoundMode.DifferentLocation =>
                prior.LastLocation is { } previousLocation
                && current.Location is { } currentLocation
                && DistanceMeters(previousLocation, currentLocation) > DifferentLocationThresholdMeters,
            KnownVehicleSoundMode.After24Hours =>
                prior.LastSeenAt is { } previousSeenAt
                && current.FirstSeenAt - previousSeenAt >= NotSeenRecentlyThreshold,
            _ => false
        };
    }

    private static double DistanceMeters(GeoPoint from, GeoPoint to)
    {
        const double earthRadiusMeters = 6_371_000;
        static double Radians(double degrees) => degrees * Math.PI / 180;

        var latitudeDelta = Radians(to.Latitude - from.Latitude);
        var longitudeDelta = Radians(to.Longitude - from.Longitude);
        var a = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2)
            + Math.Cos(Radians(from.Latitude)) * Math.Cos(Radians(to.Latitude))
            * Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        return earthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
