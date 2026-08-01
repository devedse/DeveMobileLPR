using System.Threading.Channels;

namespace DeveMobileLPR.Imaging;

public sealed class LatestFrameSlot : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Channel<byte> _available = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false
    });
    private Yuv420Frame? _latest;
    private bool _completed;
    private long _replacedFrameCount;

    public long ReplacedFrameCount => Interlocked.Read(ref _replacedFrameCount);

    public bool TryWrite(Yuv420Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Yuv420Frame? replaced;
        lock (_gate)
        {
            if (_completed)
            {
                frame.Dispose();
                return false;
            }

            replaced = _latest;
            _latest = frame;
        }

        if (replaced is not null)
        {
            Interlocked.Increment(ref _replacedFrameCount);
            replaced.Dispose();
        }
        _available.Writer.TryWrite(0);
        return true;
    }

    public void ResetStatistics() => Interlocked.Exchange(ref _replacedFrameCount, 0);

    public async ValueTask<Yuv420Frame?> ReadAsync(CancellationToken cancellationToken)
    {
        while (await _available.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _available.Reader.TryRead(out _);
            Yuv420Frame? frame;
            lock (_gate)
            {
                frame = _latest;
                _latest = null;
            }

            if (frame is not null)
            {
                return frame;
            }
        }

        lock (_gate)
        {
            var frame = _latest;
            _latest = null;
            return frame;
        }
    }

    public ValueTask DisposeAsync()
    {
        Yuv420Frame? pending;
        lock (_gate)
        {
            if (_completed)
            {
                return ValueTask.CompletedTask;
            }

            _completed = true;
            pending = _latest;
            _latest = null;
        }

        pending?.Dispose();
        _available.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
