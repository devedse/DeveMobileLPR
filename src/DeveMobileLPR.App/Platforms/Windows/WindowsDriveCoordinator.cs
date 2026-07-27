using DeveMobileLPR.Storage;

namespace DeveMobileLPR.App.Services;

internal sealed class DriveCoordinator : IAsyncDisposable
{
    private readonly SqliteSightingRepository _repository;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private DriveSnapshot _snapshot = new(
        false,
        false,
        false,
        false,
        "Preparing your private trip library…",
        false,
        null,
        0,
        [],
        null,
        [],
        false,
        [new CameraChoice("webcam", "Windows webcam · coming next")],
        "webcam");
    private bool _disposed;

    public DriveCoordinator(SqliteSightingRepository repository, AppSettings settings, RdwDatabaseService rdw)
    {
        _repository = repository;
    }

    public event EventHandler<DriveSnapshot>? SnapshotChanged;
    public SqliteSightingRepository Repository => _repository;
    public DriveSnapshot Snapshot => _snapshot;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_snapshot.IsReady)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_snapshot.IsReady)
            {
                return;
            }

            Update(_snapshot with { IsInitializing = true, Status = "Opening your private trip library…" });
            await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            Update(_snapshot with
            {
                IsInitializing = false,
                IsReady = true,
                Status = "Video analysis is ready · webcam Drive support is planned"
            });
        }
        catch (Exception exception)
        {
            Update(_snapshot with
            {
                IsInitializing = false,
                HasError = true,
                Status = $"Could not initialize the desktop app: {exception.Message}"
            });
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public Task StartDriveAsync()
    {
        Update(_snapshot with { HasError = true, Status = "Windows webcam capture is not included yet. Use Analyze for video files." });
        return Task.CompletedTask;
    }

    public Task StopDriveAsync() => Task.CompletedTask;
    public void SetZoom(float zoom) { }
    public void SelectCamera(string cameraId) { }
    public void RefreshSettings() => Update(_snapshot);
    public Task DeleteHistoryAsync() => _repository.DeleteHistoryAsync(CancellationToken.None);

    private void Update(DriveSnapshot snapshot)
    {
        _snapshot = snapshot;
        MainThread.BeginInvokeOnMainThread(() => SnapshotChanged?.Invoke(this, snapshot));
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _initializeGate.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}