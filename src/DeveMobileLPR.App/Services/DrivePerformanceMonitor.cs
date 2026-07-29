using System.Diagnostics;

namespace DeveMobileLPR.App.Services;

internal sealed record DrivePerformanceSample(
    double SourceFramesPerSecond,
    double PreviewFramesPerSecond,
    double AiFramesPerSecond);

/// <summary>
/// Samples source, presented-preview, and completed-recognition throughput once
/// per second without publishing UI work for every frame.
/// </summary>
internal sealed class DrivePerformanceMonitor : IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private readonly Timer _timer;
    private long _sourceFrames;
    private long _previewFrames;
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
        Interlocked.Exchange(ref _sourceFrames, 0);
        Interlocked.Exchange(ref _previewFrames, 0);
        Interlocked.Exchange(ref _aiFrames, 0);
        Interlocked.Exchange(ref _lastSampleTimestamp, Stopwatch.GetTimestamp());
        Volatile.Write(ref _running, 1);
        _timer.Change(SampleInterval, SampleInterval);
    }

    public void Stop()
    {
        Volatile.Write(ref _running, 0);
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _sourceFrames, 0);
        Interlocked.Exchange(ref _previewFrames, 0);
        Interlocked.Exchange(ref _aiFrames, 0);
        Interlocked.Exchange(ref _lastSampleTimestamp, 0);
    }

    /// <summary>
    /// Starts a fresh sampling window after the active video input changes so
    /// one displayed rate never combines frames from two different sources.
    /// </summary>
    public void ResetSampleWindow()
    {
        if (Volatile.Read(ref _running) == 0)
        {
            return;
        }

        Interlocked.Exchange(ref _sourceFrames, 0);
        Interlocked.Exchange(ref _previewFrames, 0);
        Interlocked.Exchange(ref _aiFrames, 0);
        Interlocked.Exchange(ref _lastSampleTimestamp, Stopwatch.GetTimestamp());
    }

    public void RecordSourceFrames(long count)
    {
        if (count > 0 && Volatile.Read(ref _running) != 0)
        {
            Interlocked.Add(ref _sourceFrames, count);
        }
    }

    public void RecordPreviewFrames(long count)
    {
        if (count > 0 && Volatile.Read(ref _running) != 0)
        {
            Interlocked.Add(ref _previewFrames, count);
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
        var sourceFrames = Interlocked.Exchange(ref _sourceFrames, 0);
        var previewFrames = Interlocked.Exchange(ref _previewFrames, 0);
        var aiFrames = Interlocked.Exchange(ref _aiFrames, 0);
        if (elapsedSeconds <= 0 || Volatile.Read(ref _running) == 0)
        {
            return;
        }

        Sampled?.Invoke(this, new DrivePerformanceSample(
            sourceFrames / elapsedSeconds,
            previewFrames / elapsedSeconds,
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
