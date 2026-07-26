namespace DeveMobileLPR.Imaging;

public sealed class LatestFrameSlot : IAsyncDisposable
{
    private readonly SemaphoreSlim _available = new(0, 1);
    private Yuv420Frame? _latest;
    private int _completed;

    public bool TryWrite(Yuv420Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Volatile.Read(ref _completed) != 0)
        {
            frame.Dispose();
            return false;
        }

        Interlocked.Exchange(ref _latest, frame)?.Dispose();
        try
        {
            _available.Release();
        }
        catch (SemaphoreFullException)
        {
            // A reader is already scheduled; it will take the newest frame.
        }

        return true;
    }

    public async ValueTask<Yuv420Frame?> ReadAsync(CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _completed) == 0)
        {
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            var frame = Interlocked.Exchange(ref _latest, null);
            if (frame is not null)
            {
                return frame;
            }
        }

        return Interlocked.Exchange(ref _latest, null);
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _completed, 1);
        Interlocked.Exchange(ref _latest, null)?.Dispose();
        try
        {
            _available.Release();
        }
        catch (SemaphoreFullException)
        {
        }

        await Task.CompletedTask;
        _available.Dispose();
    }
}
