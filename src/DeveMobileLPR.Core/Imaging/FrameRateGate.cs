namespace DeveMobileLPR.Imaging;

/// <summary>
/// Thread-safe admission gate for sources that use a monotonic timestamp. A maximum of zero means
/// unlimited. Changing the configured maximum resets the gate so a new setting takes effect immediately.
/// </summary>
public sealed class FrameRateGate
{
    private readonly long _timestampFrequency;
    private long _nextAcceptedTimestamp = long.MinValue;
    private int _maximumFramesPerSecond = int.MinValue;

    public FrameRateGate(long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        _timestampFrequency = timestampFrequency;
    }

    /// <summary>
    /// Admits a frame only when a consumer can actually take it. Demand is tested before the rate
    /// schedule on purpose: a frame refused because nobody wants it must not consume the next
    /// admission slot, or the rate limit would silently halve whenever the consumer is busy.
    /// </summary>
    public bool TryAcquire(long timestamp, int maximumFramesPerSecond, bool consumerReady) =>
        consumerReady && TryAcquire(timestamp, maximumFramesPerSecond);

    public bool TryAcquire(long timestamp, int maximumFramesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumFramesPerSecond);
        if (Interlocked.Exchange(ref _maximumFramesPerSecond, maximumFramesPerSecond) != maximumFramesPerSecond)
        {
            Interlocked.Exchange(ref _nextAcceptedTimestamp, long.MinValue);
        }

        if (maximumFramesPerSecond == 0)
        {
            return true;
        }

        var interval = _timestampFrequency / maximumFramesPerSecond;
        if (_timestampFrequency % maximumFramesPerSecond != 0)
        {
            interval++;
        }
        interval = Math.Max(1, interval);
        while (true)
        {
            var next = Interlocked.Read(ref _nextAcceptedTimestamp);
            if (timestamp < next)
            {
                return false;
            }

            var following = next == long.MinValue
                ? AddSaturating(timestamp, interval)
                : AdvanceSchedulePast(timestamp, next, interval);
            if (Interlocked.CompareExchange(ref _nextAcceptedTimestamp, following, next) == next)
            {
                return true;
            }
        }
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _maximumFramesPerSecond, int.MinValue);
        Interlocked.Exchange(ref _nextAcceptedTimestamp, long.MinValue);
    }

    private static long AddSaturating(long value, long increment) =>
        value > long.MaxValue - increment ? long.MaxValue : value + increment;

    private static long AdvanceSchedulePast(long timestamp, long next, long interval)
    {
        var steps = ((Int128)timestamp - next) / interval + 1;
        var following = (Int128)next + steps * interval;
        return following > long.MaxValue ? long.MaxValue : (long)following;
    }
}
