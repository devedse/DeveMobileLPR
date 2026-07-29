using System.Diagnostics;
using System.Threading.Channels;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Streaming;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;

namespace DeveMobileLPR.App.Platforms.Windows;

internal sealed class WindowsWebcamFrameSource : IAsyncDisposable
{
    private static readonly TimeSpan NetworkStartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NetworkPreviewCatchUpThreshold = TimeSpan.FromMilliseconds(2);
    private readonly MediaPlayerElement _preview;
    private readonly Microsoft.UI.Xaml.Controls.Image _streamPreview;
    private readonly SoftwareBitmapPreviewPresenter _streamPreviewPresenter;
    private readonly Func<int> _recognitionFramesPerSecond;
    private readonly Func<Yuv420Frame, bool> _submitFrame;
    private readonly FrameRateGate _webcamRecognitionFrameGate = new(timestampFrequency: 1000);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Channel<byte> _frameSignal = CreateSignalChannel();
    private readonly Channel<byte> _streamPreviewSignal = CreateSignalChannel();
    private readonly SemaphoreSlim _streamPreviewCapacity = new(2, 2);
    private readonly object _streamPreviewGate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _frameWorker;
    private readonly Task _streamPreviewWorker;
    private MediaCapture? _capture;
    private MediaPlayer? _player;
    private Task? _networkReaderTask;
    private CancellationTokenSource? _networkReaderCancellation;
    private MediaFrameSource? _frameSource;
    private MediaFrameReader? _reader;
    private SoftwareBitmap? _latestBitmap;
    private SoftwareBitmap? _latestStreamPreviewBitmap;
    private IReadOnlyList<CameraChoice> _cameraChoices = [];
    private string _selectedCameraId = string.Empty;
    private string _networkStreamUrl;
    private bool _analyzing;
    private bool _disposed;
    private int _previewDeactivated;
    private long _sequence;

    public WindowsWebcamFrameSource(
        MediaPlayerElement preview,
        Microsoft.UI.Xaml.Controls.Image streamPreview,
        string networkStreamUrl,
        Func<int> recognitionFramesPerSecond,
        Func<Yuv420Frame, bool> submitFrame)
    {
        _preview = preview;
        _streamPreview = streamPreview;
        _streamPreviewPresenter = new SoftwareBitmapPreviewPresenter(streamPreview);
        _streamPreview.Loaded += PreviewLoaded;
        _streamPreview.Unloaded += PreviewUnloaded;
        _networkStreamUrl = networkStreamUrl;
        _recognitionFramesPerSecond = recognitionFramesPerSecond;
        _submitFrame = submitFrame;
        _frameWorker = Task.Run(ProcessFramesAsync);
        _streamPreviewWorker = Task.Run(ProcessStreamPreviewAsync);
    }

    public event EventHandler<string>? Diagnostic;
    public event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged;
    public IReadOnlyList<CameraChoice> CameraChoices => _cameraChoices;
    public string SelectedCameraId => _selectedCameraId;
    public bool IsReady => _selectedCameraId == DriveInputIds.NetworkLlHls
        ? NetworkVideoStream.TryParse(_networkStreamUrl, out _)
        : _capture is not null;

    public void DeactivatePreview()
    {
        if (Interlocked.Exchange(ref _previewDeactivated, 1) != 0)
        {
            return;
        }

        _streamPreviewPresenter.SetPresentationActive(false);
        _streamPreview.Loaded -= PreviewLoaded;
        _streamPreview.Unloaded -= PreviewUnloaded;
        ClearPendingStreamPreview();
        _preview.SetMediaPlayer(null);
    }

    public void SetNetworkStreamUrl(string value)
    {
        _networkStreamUrl = value;
        if (_selectedCameraId != DriveInputIds.NetworkLlHls)
        {
            return;
        }

        Diagnostic?.Invoke(this, NetworkVideoStream.TryParse(value, out _)
            ? "OME LL-HLS stream ready"
            : "Enter an HTTP or HTTPS .m3u8 URL for the OME LL-HLS stream.");
    }

    public void ReportInitializationFailure(Exception exception) =>
        Diagnostic?.Invoke(this, $"Could not open the webcam: {exception.Message}");

    public async Task InitializeAsync(string preferredCameraId, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await InitializeCoreAsync(preferredCameraId, cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            if (_analyzing)
            {
                return;
            }
            if (_capture is null || _frameSource is null)
            {
                if (_selectedCameraId == DriveInputIds.NetworkLlHls)
                {
                    await StartNetworkStreamCoreAsync(cancellationToken);
                    return;
                }
                throw new InvalidOperationException("No Windows webcam is ready.");
            }

            await StartWebcamReaderCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopReaderCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cameraId) || string.Equals(cameraId, _selectedCameraId, StringComparison.Ordinal))
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var restart = _analyzing;
            await InitializeCoreAsync(cameraId, cancellationToken);
            if (restart)
            {
                if (_selectedCameraId == DriveInputIds.NetworkLlHls)
                {
                    await StartNetworkStreamCoreAsync(cancellationToken);
                }
                else
                {
                    await StartWebcamReaderCoreAsync();
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task InitializeCoreAsync(string preferredCameraId, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        var cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        _cameraChoices = cameras.Select(static camera => new CameraChoice(camera.Id, camera.Name))
            .Append(new CameraChoice(DriveInputIds.NetworkLlHls, "OME LL-HLS stream"))
            .ToArray();
        CameraChoicesChanged?.Invoke(this, _cameraChoices);

        await StopReaderCoreAsync();
        ReleaseCapture();
        ThrowIfUnavailable();
        if (preferredCameraId == DriveInputIds.NetworkLlHls || cameras.Count == 0)
        {
            _selectedCameraId = DriveInputIds.NetworkLlHls;
            _preview.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            _streamPreview.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            Diagnostic?.Invoke(this, NetworkVideoStream.TryParse(_networkStreamUrl, out _)
                ? "OME LL-HLS stream ready"
                : "Enter an HTTP or HTTPS .m3u8 URL for the OME LL-HLS stream.");
            return;
        }

        var selected = cameras.FirstOrDefault(camera => string.Equals(camera.Id, preferredCameraId, StringComparison.Ordinal)) ?? cameras[0];
        var capture = new MediaCapture();
        MediaPlayer? player = null;
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = selected.Id,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu
            });
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfUnavailable();

            var source = capture.FrameSources.Values.FirstOrDefault(static item =>
                    item.Info.SourceKind == MediaFrameSourceKind.Color && item.Info.MediaStreamType == MediaStreamType.VideoPreview)
                ?? capture.FrameSources.Values.FirstOrDefault(static item =>
                    item.Info.SourceKind == MediaFrameSourceKind.Color && item.Info.MediaStreamType == MediaStreamType.VideoRecord)
                ?? throw new InvalidOperationException("The selected webcam has no color video stream.");
            player = new MediaPlayer
            {
                AutoPlay = true,
                RealTimePlayback = true,
                Source = MediaSource.CreateFromMediaFrameSource(source)
            };
            _capture = capture;
            _frameSource = source;
            _player = player;
            _selectedCameraId = selected.Id;
            _preview.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            _streamPreview.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            if (IsPreviewActive)
            {
                _preview.SetMediaPlayer(player);
                player.Play();
            }
            Diagnostic?.Invoke(this, $"Camera ready · {selected.Name}");
        }
        catch
        {
            if (ReferenceEquals(_capture, capture))
            {
                _capture = null;
                _frameSource = null;
                _player = null;
                _selectedCameraId = string.Empty;
                try
                {
                    _preview.SetMediaPlayer(null);
                }
                catch (Exception cleanupException)
                {
                    Debug.WriteLine($"Could not detach the failed webcam preview: {cleanupException}");
                }
            }

            player?.Dispose();
            capture.Dispose();
            throw;
        }
    }

    private async Task StartWebcamReaderCoreAsync()
    {
        _webcamRecognitionFrameGate.Reset();
        _reader = await _capture!.CreateFrameReaderAsync(_frameSource!, MediaEncodingSubtypes.Bgra8);
        _reader.FrameArrived += FrameArrived;
        var status = await _reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            _reader.FrameArrived -= FrameArrived;
            _reader.Dispose();
            _reader = null;
            throw new InvalidOperationException($"The webcam frame reader could not start ({status}).");
        }

        _analyzing = true;
        Diagnostic?.Invoke(this, "Camera active · processing stays on this device");
    }

    private async Task StartNetworkStreamCoreAsync(CancellationToken cancellationToken)
    {
        if (!NetworkVideoStream.TryParse(_networkStreamUrl, out var stream))
        {
            throw new InvalidOperationException("Enter a valid HTTP or HTTPS .m3u8 URL before starting the OME stream.");
        }

        var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
        _networkReaderCancellation = readerCancellation;
        WindowsMediaFoundationLiveFrameReader? reader = null;
        try
        {
            using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                readerCancellation.Token,
                cancellationToken);
            startupCancellation.CancelAfter(NetworkStartupTimeout);

            var feed = new HlsCompletedSegmentFeed(stream!.Uri);
            var first = await feed.GetNextAsync(startupCancellation.Token);
            reader = await WindowsMediaFoundationLiveFrameReader.CreateForSegmentAsync(
                first.Initialization,
                first.Media,
                startupCancellation.Token);
            ThrowIfUnavailable();
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ownedReader = reader;
            reader = null;

            _preview.SetMediaPlayer(null);
            _preview.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            _streamPreview.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            _analyzing = true;
            _networkReaderTask = Task.Run(
                () => ReadNetworkFramesAsync(feed, ownedReader, opened, readerCancellation.Token),
                CancellationToken.None);
            await opened.Task.WaitAsync(startupCancellation.Token);
            Diagnostic?.Invoke(
                this,
                $"OME stream active · adaptive native NV12 preview · {FormatRecognitionRate(_recognitionFramesPerSecond())} recognition");
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested
            && !_cancellation.IsCancellationRequested)
        {
            reader?.Dispose();
            await StopReaderCoreAsync();
            throw new TimeoutException(
                $"The OME stream did not deliver a decodable video frame within {NetworkStartupTimeout.TotalSeconds:0} seconds.",
                exception);
        }
        catch
        {
            reader?.Dispose();
            await StopReaderCoreAsync();
            throw;
        }
    }

    private async Task ReadNetworkFramesAsync(
        HlsCompletedSegmentFeed feed,
        WindowsMediaFoundationLiveFrameReader initialReader,
        TaskCompletionSource opened,
        CancellationToken cancellationToken)
    {
        var reader = initialReader;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var segmentClock = new Stopwatch();
                long? segmentStartTimestamp = null;
                var analysisGate = new FrameRateGate(TimeSpan.TicksPerSecond);
                var skipPreviewForCatchUp = false;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var previewReserved = IsPreviewActive
                        && !skipPreviewForCatchUp
                        && _streamPreviewCapacity.Wait(0);
                    try
                    {
                        using var decodedFrame = reader.ReadNext(
                            previewReserved,
                            analysisGate,
                            _recognitionFramesPerSecond(),
                            Interlocked.Increment(ref _sequence),
                            DateTimeOffset.UtcNow,
                            cancellationToken);
                        if (decodedFrame is null)
                        {
                            break;
                        }

                        opened.TrySetResult();

                        var timestamp = decodedFrame.Timestamp;
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
                            PublishStreamPreview(previewBitmap);
                            previewReserved = false;
                        }
                    }
                    finally
                    {
                        if (previewReserved)
                        {
                            _streamPreviewCapacity.Release();
                        }
                    }
                }

                reader.Dispose();
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
            Diagnostic?.Invoke(this, $"OME software decoding failed: {exception.Message}");
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

        return -delay > NetworkPreviewCatchUpThreshold;
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

    private bool IsPreviewActive => _streamPreviewPresenter.IsPresentationActive
        && Volatile.Read(ref _previewDeactivated) == 0;

    private void ThrowIfUnavailable() => ObjectDisposedException.ThrowIf(
        _disposed || Volatile.Read(ref _previewDeactivated) != 0,
        this);

    private void PreviewLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs args)
    {
        if (Volatile.Read(ref _previewDeactivated) != 0)
        {
            return;
        }

        _streamPreviewPresenter.SetPresentationActive(true);
        if (_player is not null)
        {
            _preview.SetMediaPlayer(_player);
            _player.Play();
        }
    }

    private void PreviewUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs args)
    {
        _streamPreviewPresenter.SetPresentationActive(false);
        _preview.SetMediaPlayer(null);
        ClearPendingStreamPreview();
    }

    private void SignalFrameAvailable()
    {
        _frameSignal.Writer.TryWrite(0);
    }

    private void FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var reference = sender.TryAcquireLatestFrame();
        var bitmap = reference?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null)
        {
            return;
        }
        if (!_webcamRecognitionFrameGate.TryAcquire(
                Environment.TickCount64,
                _recognitionFramesPerSecond()))
        {
            return;
        }

        var owned = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? SoftwareBitmap.Copy(bitmap)
            : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        Interlocked.Exchange(ref _latestBitmap, owned)?.Dispose();
        SignalFrameAvailable();
    }

    private async Task ProcessFramesAsync()
    {
        try
        {
            while (await _frameSignal.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                _frameSignal.Reader.TryRead(out _);
                using var bitmap = Interlocked.Exchange(ref _latestBitmap, null);
                if (bitmap is null)
                {
                    continue;
                }

                var frame = WindowsSoftwareBitmapConverter.ToYuv420Frame(
                    bitmap,
                    Interlocked.Increment(ref _sequence),
                    DateTimeOffset.UtcNow);
                _submitFrame(frame);
            }
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke(this, $"Camera frame processing failed: {exception.Message}");
        }
    }

    private void PublishStreamPreview(SoftwareBitmap bitmap)
    {
        SoftwareBitmap? replaced;
        lock (_streamPreviewGate)
        {
            if (!IsPreviewActive)
            {
                bitmap.Dispose();
                _streamPreviewCapacity.Release();
                return;
            }

            replaced = _latestStreamPreviewBitmap;
            _latestStreamPreviewBitmap = bitmap;
        }

        if (replaced is not null)
        {
            replaced.Dispose();
            _streamPreviewCapacity.Release();
        }
        _streamPreviewSignal.Writer.TryWrite(0);
    }

    private async Task ProcessStreamPreviewAsync()
    {
        try
        {
            while (await _streamPreviewSignal.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                _streamPreviewSignal.Reader.TryRead(out _);
                SoftwareBitmap? pending;
                lock (_streamPreviewGate)
                {
                    pending = _latestStreamPreviewBitmap;
                    _latestStreamPreviewBitmap = null;
                }

                if (pending is not null)
                {
                    try
                    {
                        await _streamPreviewPresenter.PresentAsync(pending).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        Diagnostic?.Invoke(this, $"OME preview rendering failed: {exception.Message}");
                    }
                    finally
                    {
                        _streamPreviewCapacity.Release();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke(this, $"OME preview worker failed: {exception.Message}");
        }
    }

    private async Task StopReaderCoreAsync()
    {
        var clearStreamPreview = _selectedCameraId == DriveInputIds.NetworkLlHls;
        _analyzing = false;
        _webcamRecognitionFrameGate.Reset();
        var networkCancellation = _networkReaderCancellation;
        var networkTask = _networkReaderTask;
        _networkReaderCancellation = null;
        _networkReaderTask = null;
        if (networkCancellation is not null)
        {
            networkCancellation.Cancel();
            if (networkTask is not null)
            {
                try { await networkTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            networkCancellation.Dispose();
        }
        if (_reader is null)
        {
            ReleasePlayer();
            Interlocked.Exchange(ref _latestBitmap, null)?.Dispose();
            ClearPendingStreamPreview();
            if (clearStreamPreview)
            {
                await _streamPreviewPresenter.ClearAsync().ConfigureAwait(false);
            }
            return;
        }

        _reader.FrameArrived -= FrameArrived;
        await _reader.StopAsync();
        _reader.Dispose();
        _reader = null;
        Interlocked.Exchange(ref _latestBitmap, null)?.Dispose();
        ClearPendingStreamPreview();
        if (clearStreamPreview)
        {
            await _streamPreviewPresenter.ClearAsync().ConfigureAwait(false);
        }
    }

    private void ClearPendingStreamPreview()
    {
        SoftwareBitmap? pending;
        lock (_streamPreviewGate)
        {
            pending = _latestStreamPreviewBitmap;
            _latestStreamPreviewBitmap = null;
        }

        if (pending is not null)
        {
            pending.Dispose();
            _streamPreviewCapacity.Release();
        }
    }

    private void ReleaseCapture()
    {
        _preview.SetMediaPlayer(null);
        ReleasePlayer();
        _frameSource = null;
        _capture?.Dispose();
        _capture = null;
    }

    private void ReleasePlayer()
    {
        if (_player is null)
        {
            return;
        }

        _player.Dispose();
        _player = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _lifecycleGate.WaitAsync();
        try
        {
            await StopReaderCoreAsync();
            ReleaseCapture();
            _cancellation.Cancel();
            _frameSignal.Writer.TryComplete();
            _streamPreviewSignal.Writer.TryComplete();
        }
        finally
        {
            _lifecycleGate.Release();
        }
        try { await _frameWorker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await _streamPreviewWorker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        Interlocked.Exchange(ref _latestBitmap, null)?.Dispose();
        ClearPendingStreamPreview();
        await _streamPreviewPresenter.DisposeAsync().ConfigureAwait(false);
        _cancellation.Dispose();
        _streamPreviewCapacity.Dispose();
        _lifecycleGate.Dispose();
    }
}
