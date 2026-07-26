using Android;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.AndroidApp.Infrastructure;

internal sealed class AndroidLocationTracker : Java.Lang.Object, ILocationListener
{
    private readonly Context _context;
    private readonly LocationManager _manager;
    private GeoPoint? _latest;
    private bool _running;

    public AndroidLocationTracker(Context context)
    {
        _context = context;
        _manager = (LocationManager?)context.GetSystemService(Context.LocationService)
            ?? throw new InvalidOperationException("Android location service is unavailable.");
    }

    public GeoPoint? Latest => _latest;
    public bool IsRunning => _running;
    public event EventHandler<GeoPoint>? LocationChanged;

    public bool Start()
    {
        if (_context.CheckSelfPermission(Manifest.Permission.AccessFineLocation) != Permission.Granted)
        {
            return false;
        }

        if (_running)
        {
            return true;
        }

        _manager.RequestLocationUpdates(LocationManager.GpsProvider, 1_000, 2f, this);
        _running = true;
        return true;
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _manager.RemoveUpdates(this);
        _running = false;
    }

    public void OnLocationChanged(global::Android.Locations.Location location)
    {
        _latest = new GeoPoint(location.Latitude, location.Longitude, location.HasAccuracy ? location.Accuracy : null);
        LocationChanged?.Invoke(this, _latest.Value);
    }

    public void OnProviderDisabled(string provider) { }
    public void OnProviderEnabled(string provider) { }
#pragma warning disable CS0618
    public void OnStatusChanged(string? provider, Availability status, global::Android.OS.Bundle? extras) { }
#pragma warning restore CS0618
}
