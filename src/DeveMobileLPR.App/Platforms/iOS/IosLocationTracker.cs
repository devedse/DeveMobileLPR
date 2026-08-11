using DeveMobileLPR.Application;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.Infrastructure;

internal sealed class IosLocationTrackerFactory : IDriveLocationTrackerFactory
{
    public IDriveLocationTracker Create() => new IosLocationTracker();
}

internal sealed class IosLocationTracker : IDriveLocationTracker
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private LocationFix? _latest;

    public LocationFix? Latest { get { lock (_gate) return _latest; } }

    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var permission = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        if (permission != PermissionStatus.Granted) return false;
        if (_cancellation is not null) return true;

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = TrackAsync(_cancellation.Token);
        return true;
    }

    private async Task TrackAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var location = await Geolocation.Default.GetLocationAsync(
                    new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(10)),
                    cancellationToken);
                if (location is not null)
                {
                    var fix = new LocationFix(
                        new GeoPoint(location.Latitude, location.Longitude, (float?)location.Accuracy),
                        location.Timestamp.ToUniversalTime());
                    lock (_gate) _latest = fix;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"iOS location update failed: {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    public void Stop()
    {
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        lock (_gate) _latest = null;
    }

    public void Dispose() => Stop();
}
