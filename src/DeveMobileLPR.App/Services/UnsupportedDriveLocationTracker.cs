using DeveMobileLPR.Application;

namespace DeveMobileLPR.App.Services;

/// <summary>
/// Shared fallback for targets where drive-location tracking is not implemented.
/// </summary>
internal sealed class UnsupportedDriveLocationTrackerFactory : IDriveLocationTrackerFactory
{
    public IDriveLocationTracker Create() => new UnsupportedDriveLocationTracker();
}

internal sealed class UnsupportedDriveLocationTracker : IDriveLocationTracker
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
