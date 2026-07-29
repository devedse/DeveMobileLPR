using System.Diagnostics;

namespace DeveMobileLPR.App.Services;

internal sealed record DrivePerformanceSample(
    double VideoFramesPerSecond,
    double AiFramesPerSecond);

/// <summary>
/// Samples video and completed-recognition throughput once per second without
/// publishing UI work for every frame.
/// </summary>
internal sealed class DrivePerformanceMonitor : IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private readonly Timer _timer;
    private long _videoFrames;
    private long _aiFrames;
    private long _lastSampleTimestamp;
    private int _running;
    private int _disposed;

    public DrivePerformanceMonitor()
    {
        _timer = new Timer(Sample, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler<DrivePerformanceSample>? Sampled;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Interlocked.Exchange(ref _videoFrames, 0);
        Interlocked.Exchange(ref _aiFrames, 0);
        Interlocked.Exchange(ref _lastSampleTimestamp, Stopwatch.GetTimestamp());
        Volatile.Write(ref _running, 1);
        _timer.Change(SampleInterval, SampleInterval);
    }

    public void Stop()
    {
        Volatile.Write(ref _running, 0);
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _videoFrames, 0);
        Interlocked.Exchange(ref _aiFrames, 0);
        Interlocked.Exchange(ref _lastSampleTimestamp, 0);
    }

    public void RecordVideoFrame()
    {
        if (Volatile.Read(ref _running) != 0)
        {
            Interlocked.Increment(ref _videoFrames);
        }
    }

    public void RecordAiFrame()
    {
        if (Volatile.Read(ref _running) != 0)
        {
            Interlocked.Increment(ref _aiFrames);
        }
    }

    private void Sample(object? state)
    {
        if (Volatile.Read(ref _running) == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref _lastSampleTimestamp, now);
        var elapsedSeconds = previous == 0
            ? 0
            : Stopwatch.GetElapsedTime(previous, now).TotalSeconds;
        var videoFrames = Interlocked.Exchange(ref _videoFrames, 0);
        var aiFrames = Interlocked.Exchange(ref _aiFrames, 0);
        if (elapsedSeconds <= 0 || Volatile.Read(ref _running) == 0)
        {
            return;
        }

        Sampled?.Invoke(this, new DrivePerformanceSample(
            videoFrames / elapsedSeconds,
            aiFrames / elapsedSeconds));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
        _timer.Dispose();
    }
}
