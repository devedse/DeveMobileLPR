namespace DeveMobileLPR.Streaming;

/// <summary>
/// Decides when a live stream has drifted far enough behind the broadcast edge that jumping back
/// to it beats waiting for playback to make the time up.
/// </summary>
/// <remarks>
/// Both platforms drift for the same reason — producing frames costs more than real time, so the
/// deficit accumulates — but neither recovers on its own. Media3 trims playback speed by a few
/// percent, which takes minutes to recover seconds, and the Windows segment cursor simply walks the
/// playlist in order and never catches up at all. The mechanism for resynchronising differs per
/// platform; this is the shared judgement of <em>when</em> to do it, and the rate limit that stops a
/// stream that cannot keep up from resynchronising on a loop.
/// </remarks>
public sealed class LiveStreamLatencyPolicy
{
    /// <summary>
    /// Drift allowed before resynchronising. Generous enough to absorb a brief network stall or a
    /// few slow frames, small enough that a driver is not looking at the recent past.
    /// </summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Minimum gap between resynchronisations. A resync discards buffered video, so repeating it
    /// every evaluation would replace smooth-but-late playback with a stutter that is never late.
    /// </summary>
    public static readonly TimeSpan DefaultMinimumInterval = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _budget;
    private readonly TimeSpan _minimumInterval;
    private DateTimeOffset? _lastResyncAt;

    public LiveStreamLatencyPolicy(TimeSpan? budget = null, TimeSpan? minimumInterval = null)
    {
        _budget = budget ?? DefaultBudget;
        _minimumInterval = minimumInterval ?? DefaultMinimumInterval;
        if (_budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }
        if (_minimumInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        }
    }

    public TimeSpan Budget => _budget;

    /// <summary>
    /// True when <paramref name="drift"/> exceeds the budget and enough time has passed since the
    /// last resynchronisation. Returning true records the decision, so a caller that acts on it does
    /// not need to report back.
    /// </summary>
    public bool ShouldResync(TimeSpan drift, DateTimeOffset now)
    {
        if (drift <= _budget)
        {
            return false;
        }

        if (_lastResyncAt is { } last && now - last < _minimumInterval)
        {
            return false;
        }

        _lastResyncAt = now;
        return true;
    }

    /// <summary>Forgets the resync history, for a stream that is starting fresh.</summary>
    public void Reset() => _lastResyncAt = null;
}
