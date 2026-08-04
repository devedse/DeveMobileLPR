using Android;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Application;

namespace DeveMobileLPR.App.Infrastructure;

internal sealed class AndroidLocationTrackerFactory(Context context) : IDriveLocationTrackerFactory
{
    public IDriveLocationTracker Create() => new AndroidLocationTracker(context);
}

/// <summary>
/// Reports the device position for one drive.
/// </summary>
/// <remarks>
/// Subscribes to every enabled provider rather than GPS alone. A cold GPS fix takes tens of seconds,
/// so a short drive used to finish before the first one arrived; the network and fused providers
/// answer in about a second, which is coarse but placed on the right street. Each fix carries the
/// time the platform observed it, so a consumer can reject one that is no longer current.
/// </remarks>
internal sealed class AndroidLocationTracker : Java.Lang.Object, ILocationListener, IDriveLocationTracker
{
    private const long MinimumUpdateIntervalMilliseconds = 1_000;
    private const float MinimumUpdateDistanceMeters = 2;

    private readonly Context _context;
    private readonly LocationManager _manager;
    private readonly object _gate = new();
    private LocationFix? _latest;
    private bool _running;

    public AndroidLocationTracker(Context context)
    {
        _context = context;
        _manager = (LocationManager?)context.GetSystemService(Context.LocationService)
            ?? throw new InvalidOperationException("Android location service is unavailable.");
    }

    /// <summary>Reads under a lock because a fix is a multi-word struct written by the platform callback.</summary>
    public LocationFix? Latest { get { lock (_gate) return _latest; } }

    private bool Start()
    {
        if (_context.CheckSelfPermission(Manifest.Permission.AccessFineLocation) != Permission.Granted)
        {
            return false;
        }

        if (_running)
        {
            return true;
        }

        var providers = EnabledProviders();
        if (providers.Count == 0)
        {
            return false;
        }

        foreach (var provider in providers)
        {
            // A cached fix is worth having: it is stamped with its real age, so a consumer that
            // cares about freshness can still discard it.
            Observe(_manager.GetLastKnownLocation(provider));
            _manager.RequestLocationUpdates(
                provider,
                MinimumUpdateIntervalMilliseconds,
                MinimumUpdateDistanceMeters,
                this);
        }

        _running = true;
        return true;
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var permission = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        cancellationToken.ThrowIfCancellationRequested();
        return permission == PermissionStatus.Granted && Start();
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _manager.RemoveUpdates(this);
        _running = false;
        lock (_gate) _latest = null;
    }

    public void OnLocationChanged(global::Android.Locations.Location location) => Observe(location);

    public void OnProviderDisabled(string provider) { }
    public void OnProviderEnabled(string provider) { }
#pragma warning disable CS0618
    public void OnStatusChanged(string? provider, Availability status, global::Android.OS.Bundle? extras) { }
#pragma warning restore CS0618

    /// <summary>
    /// Keeps the most recently observed fix. Several providers report at once and the coarse ones
    /// answer first, so ordering by observation time is what stops a stale cached fix from
    /// overwriting a live one.
    /// </summary>
    private void Observe(global::Android.Locations.Location? location)
    {
        if (location is null)
        {
            return;
        }

        var fix = new LocationFix(
            new GeoPoint(
                location.Latitude,
                location.Longitude,
                location.HasAccuracy ? location.Accuracy : null),
            ObservedAt(location));
        lock (_gate)
        {
            if (_latest is { } current && current.ObservedAt > fix.ObservedAt)
            {
                return;
            }

            _latest = fix;
        }
    }

    /// <summary>
    /// Android reports the fix time as Unix milliseconds. A device that has not set its clock can
    /// report zero or something implausible, in which case treating the fix as observed now is
    /// closer to the truth than a date in 1970 that every freshness check would reject.
    /// </summary>
    private static DateTimeOffset ObservedAt(global::Android.Locations.Location location)
    {
        if (location.Time <= 0)
        {
            return DateTimeOffset.UtcNow;
        }

        var reported = DateTimeOffset.FromUnixTimeMilliseconds(location.Time);
        return reported > DateTimeOffset.UnixEpoch ? reported : DateTimeOffset.UtcNow;
    }

    private List<string> EnabledProviders()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            candidates.Add(LocationManager.FusedProvider);
        }
        candidates.Add(LocationManager.GpsProvider);
        candidates.Add(LocationManager.NetworkProvider);

        var available = _manager.GetProviders(enabledOnly: true) ?? [];
        return candidates.Where(available.Contains).Distinct(StringComparer.Ordinal).ToList();
    }
}
