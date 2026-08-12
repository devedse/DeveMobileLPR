using System.Diagnostics;
using System.Threading.Channels;
using DeveMobileLPR.Application;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Streaming;
using Windows.Graphics.Imaging;

namespace DeveMobileLPR.App.Platforms.Windows.Camera;

/// <summary>
/// Owns Windows LL-HLS transport, Media Foundation decoding, preview presentation, and
/// recognition-frame sampling. The parent <see cref="WindowsDriveVideoInput"/> only selects
/// between this source and the physical webcam source.
/// </summary>
internal sealed class WindowsHlsFrameSource : IAsyncDisposable, IDriveFrameSourceTelemetry
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PreviewCatchUpThreshold = TimeSpan.FromMilliseconds(2);
    private readonly Microsoft.UI.Xaml.Controls.Image _preview;
    private readonly SoftwareBitmapPreviewPresenter _previewPresenter;
    private readonly Func<int> _recognitionFramesPerSecond;
    private readonly Func<bool> _hasPendingRecognitionFrame;
    private readonly Func<Yuv420Frame, bool> _submitFrame;
    private readonly Func<long> _nextSequence;
    private readonly Channel<byte> _previewSignal = CreateSignalChannel();
    private readonly SemaphoreSlim _previewCapacity = new(2, 2);
    private readonly object _previewGate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Task _previewWorker;
    private Task? _readerTask;
    private CancellationTokenSource? _readerCancellation;
    private SoftwareBitmap? _latestPreviewBitmap;
    private string _streamUrl;
    private bool _disposed;
    private int _previewDeactivated;

    public WindowsHlsFrameSource(
        Microsoft.UI.Xaml.Controls.Image preview,
        string streamUrl,
        Func<int> recognitionFramesPerSecond,
        Func<bool> hasPendingRecognitionFrame,
        Func<Yuv420Frame, bool> submitFrame,
        Func<long> nextSequence)
    {
        _preview = preview;
        _streamUrl = streamUrl;
        _recognitionFramesPerSecond = recognitionFramesPerSecond;
        _hasPendingRecognitionFrame = hasPendingRecognitionFrame;
        _submitFrame = submitFrame;
        _nextSequence = nextSequence;
        _previewPresenter = new SoftwareBitmapPreviewPresenter(preview);
        _preview.Loaded += PreviewLoaded;
        _preview.Unloaded += PreviewUnloaded;
        _previewWorker = Task.Run(ProcessPreviewAsync);
    }

    public event EventHandler<DriveInputDiagnostic>? Diagnostic;
    public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
    public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented;

    public bool ReportsPreviewFrames => true;

    public bool IsReady => NetworkVideoStream.TryParse(_streamUrl, out _);

    public string ReadinessMessage => IsReady
        ? "OME LL-HLS stream ready"
        : "Enter an HTTP or HTTPS .m3u8 URL for the OME LL-HLS stream.";

    public void SetStreamUrl(string value) => _streamUrl = value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!NetworkVideoStream.TryParse(_streamUrl, out var stream))
        {
            throw new InvalidOperationException("Enter a valid HTTP or HTTPS .m3u8 URL before starting the OME stream.");
        }

        if (_readerCancellation is not null)
        {
            return;
        }

        var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _readerCancellation = readerCancellation;
        WindowsMediaFoundationLiveFrameReader? reader = null;
        try
        {
            using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                readerCancellation.Token,
                cancellationToken);
            startupCancellation.CancelAfter(StartupTimeout);

            var feed = new HlsCompletedSegmentFeed(stream!.Uri);
            var first = await feed.GetNextAsync(startupCancellation.Token);
            reader = await WindowsMediaFoundationLiveFrameReader.CreateForSegmentAsync(
                first.Initialization,
                first.Media,
                startupCancellation.Token);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ownedReader = reader;
            reader = null;

            _readerTask = Task.Run(
                () => ReadFramesAsync(feed, ownedReader, opened, readerCancellation.Token),
                CancellationToken.None);
            await opened.Task.WaitAsync(startupCancellation.Token);
            Diagnostic?.Invoke(
                this,
                new DriveInputDiagnostic(
                    $"OME stream active · adaptive native NV12 preview · {FormatRecognitionRate(_recognitionFramesPerSecond())} recognition"));
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested
            && !_lifetimeCancellation.IsCancellationRequested)
        {
            reader?.Dispose();
            await StopAsync();
            throw new TimeoutException(
                $"The OME stream did not deliver a decodable video frame within {StartupTimeout.TotalSeconds:0} seconds.",
                exception);
        }
        catch
        {
            reader?.Dispose();
            await StopAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        var readerCancellation = _readerCancellation;
        var readerTask = _readerTask;
        _readerCancellation = null;
        _readerTask = null;
        if (readerCancellation is not null)
        {
            readerCancellation.Cancel();
            if (readerTask is not null)
            {
                try
                {
                    await readerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            readerCancellation.Dispose();
        }

        ClearPendingPreview();
        await _previewPresenter.ClearAsync().ConfigureAwait(false);
    }

    public void DeactivatePreview()
    {
        if (Interlocked.Exchange(ref _previewDeactivated, 1) != 0)
        {
            return;
        }

        _previewPresenter.SetPresentationActive(false);
        _preview.Loaded -= PreviewLoaded;
        _preview.Unloaded -= PreviewUnloaded;
        ClearPendingPreview();
    }

    private async Task ReadFramesAsync(
        HlsCompletedSegmentFeed feed,
        WindowsMediaFoundationLiveFrameReader initialReader,
        TaskCompletionSource opened,
        CancellationToken cancellationToken)
    {
        var reader = initialReader;
        var latencyPolicy = new LiveStreamLatencyPolicy();
        var streamClock = Stopwatch.StartNew();
        var playedDuration = TimeSpan.Zero;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var segmentClock = new Stopwatch();
                long? segmentStartTimestamp = null;
                long? segmentLastTimestamp = null;
                var analysisGate = new FrameRateGate(TimeSpan.TicksPerSecond);
                var skipPreviewForCatchUp = false;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var previewReserved = IsPreviewActive
                        && !skipPreviewForCatchUp
                        && _previewCapacity.Wait(0);
                    try
                    {
                        using var decodedFrame = reader.ReadNext(
                            previewReserved,
                            analysisGate,
                            _recognitionFramesPerSecond(),
                            !_hasPendingRecognitionFrame(),
                            _nextSequence(),
                            DateTimeOffset.UtcNow,
                            cancellationToken);
                        if (decodedFrame is null)
                        {
                            break;
                        }

                        SourceFramesAvailable?.Invoke(this, new DriveFrameCountEventArgs(1));
                        opened.TrySetResult();

                        var timestamp = decodedFrame.Timestamp;
                        segmentLastTimestamp = timestamp;
                        if (segmentStartTimestamp is null)
                        {
                            segmentStartTimestamp = timestamp;
                            segmentClock.Restart();
                            skipPreviewForCatchUp = false;
                        }
                        else
                        {
                            skipPreviewForCatchUp = await DelayUntilMediaTimeAsync(
                                segmentClock,
                                segmentStartTimestamp.Value,
                                timestamp,
                                cancellationToken).ConfigureAwait(false);
                        }

                        var analysisFrame = decodedFrame.DetachAnalysisFrame();
                        if (analysisFrame is not null)
                        {
                            try
                            {
                                _submitFrame(analysisFrame);
                            }
                            catch
                            {
                                analysisFrame.Dispose();
                                throw;
                            }
                        }

                        var previewBitmap = decodedFrame.DetachPreviewBitmap();
                        if (previewBitmap is not null)
                        {
                            PublishPreview(previewBitmap);
                            previewReserved = false;
                        }
                    }
                    finally
                    {
                        if (previewReserved)
                        {
                            _previewCapacity.Release();
                        }
                    }
                }

                reader.Dispose();
                if (segmentStartTimestamp is { } start && segmentLastTimestamp is { } end && end > start)
                {
                    playedDuration += TimeSpan.FromTicks(end - start);
                }

                var drift = streamClock.Elapsed - playedDuration;
                if (latencyPolicy.ShouldResync(drift, DateTimeOffset.UtcNow))
                {
                    feed.SkipToLiveEdge();
                    streamClock.Restart();
                    playedDuration = TimeSpan.Zero;
                    Diagnostic?.Invoke(this, new DriveInputDiagnostic("OME stream rejoined the live edge"));
                }

                var next = await feed.GetNextAsync(cancellationToken).ConfigureAwait(false);
                reader = await WindowsMediaFoundationLiveFrameReader.CreateForSegmentAsync(
                    next.Initialization,
                    next.Media,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            opened.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            opened.TrySetException(exception);
            Diagnostic?.Invoke(this, new DriveInputDiagnostic($"OME software decoding failed: {exception.Message}", true));
        }
        finally
        {
            reader.Dispose();
        }
    }

    private static async Task<bool> DelayUntilMediaTimeAsync(
        Stopwatch segmentClock,
        long segmentStartTimestamp,
        long frameTimestamp,
        CancellationToken cancellationToken)
    {
        var mediaElapsedTicks = frameTimestamp - segmentStartTimestamp;
        if (mediaElapsedTicks <= 0)
        {
            return false;
        }

        var delay = TimeSpan.FromTicks(mediaElapsedTicks) - segmentClock.Elapsed;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return false;
        }

        return -delay > PreviewCatchUpThreshold;
    }

    private static string FormatRecognitionRate(int maximumFramesPerSecond) =>
        maximumFramesPerSecond == 0 ? "unlimited" : $"max {maximumFramesPerSecond} FPS";

    private static Channel<byte> CreateSignalChannel() => Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false
    });

    private bool IsPreviewActive => _previewPresenter.IsPresentationActive
        && Volatile.Read(ref _previewDeactivated) == 0;

    private void PreviewLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs args)
    {
        if (Volatile.Read(ref _previewDeactivated) == 0)
        {
            _previewPresenter.SetPresentationActive(true);
        }
    }

    private void PreviewUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs args)
    {
        _previewPresenter.SetPresentationActive(false);
        ClearPendingPreview();
    }

    private void PublishPreview(SoftwareBitmap bitmap)
    {
        SoftwareBitmap? replaced;
        lock (_previewGate)
        {
            if (!IsPreviewActive)
            {
                bitmap.Dispose();
                _previewCapacity.Release();
                return;
            }

            replaced = _latestPreviewBitmap;
            _latestPreviewBitmap = bitmap;
        }

        if (replaced is not null)
        {
            replaced.Dispose();
            _previewCapacity.Release();
        }
        _previewSignal.Writer.TryWrite(0);
    }

    private async Task ProcessPreviewAsync()
    {
        try
        {
            while (await _previewSignal.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                _previewSignal.Reader.TryRead(out _);
                SoftwareBitmap? pending;
                lock (_previewGate)
                {
                    pending = _latestPreviewBitmap;
                    _latestPreviewBitmap = null;
                }

                if (pending is not null)
                {
                    try
                    {
                        await _previewPresenter.PresentAsync(pending).ConfigureAwait(false);
                        PreviewFramesPresented?.Invoke(this, new DriveFrameCountEventArgs(1));
                    }
                    catch (Exception exception)
                    {
                        Diagnostic?.Invoke(this, new DriveInputDiagnostic($"OME preview rendering failed: {exception.Message}", true));
                    }
                    finally
                    {
                        _previewCapacity.Release();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke(this, new DriveInputDiagnostic($"OME preview worker failed: {exception.Message}", true));
        }
    }

    private void ClearPendingPreview()
    {
        SoftwareBitmap? pending;
        lock (_previewGate)
        {
            pending = _latestPreviewBitmap;
            _latestPreviewBitmap = null;
        }

        if (pending is not null)
        {
            pending.Dispose();
            _previewCapacity.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync();
        DeactivatePreview();
        _lifetimeCancellation.Cancel();
        _previewSignal.Writer.TryComplete();
        try
        {
            await _previewWorker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        ClearPendingPreview();
        await _previewPresenter.DisposeAsync().ConfigureAwait(false);
        _lifetimeCancellation.Dispose();
        _previewCapacity.Dispose();
    }
}
