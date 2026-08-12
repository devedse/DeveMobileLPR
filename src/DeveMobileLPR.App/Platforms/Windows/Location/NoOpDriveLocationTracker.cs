using DeveMobileLPR.Application;

namespace DeveMobileLPR.App.Platforms.Windows.Location;

internal sealed class NoOpDriveLocationTrackerFactory : IDriveLocationTrackerFactory
{
    public IDriveLocationTracker Create() => new NoOpDriveLocationTracker();
}

internal sealed class NoOpDriveLocationTracker : IDriveLocationTracker
{
    public LocationFix? Latest => null;

    public Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public void Stop() { }

    public void Dispose() { }
}
