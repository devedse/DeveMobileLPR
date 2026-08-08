using System.Buffers;
using Android.Content;
using Android.Graphics;
using Android.Views;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Video;
using DeveMobileLPR.Application;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Streaming;
using Media3Format = AndroidX.Media3.Common.Format;

namespace DeveMobileLPR.App.Camera;

/// <summary>
/// Plays an LL-HLS stream through Android's hardware-backed Media3 player and
/// samples the same decoded texture for recognition. This keeps preview and AI
/// on one network connection and one decoder.
/// </summary>
internal sealed class AndroidHlsFrameSource : Java.Lang.Object, TextureView.ISurfaceTextureListener, IDriveFrameSourceTelemetry
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private readonly Context _context;
    private readonly AndroidVideoTextureView _preview;
    private readonly Func<int> _recognitionFramesPerSecond;
    private readonly Func<bool> _hasPendingRecognitionFrame;
    private readonly Func<Yuv420Frame, bool> _submitFrame;
    private readonly FrameRateGate _recognitionFrameGate = new(timestampFrequency: 1000);
    private readonly AndroidHlsVideoMetadataListener _metadataListener;
    private readonly AndroidHlsPlayerErrorListener _playerErrorListener;
    private readonly LiveStreamLatencyPolicy _latencyPolicy = new();
    private IExoPlayer? _player;
    private Surface? _surface;
    private TaskCompletionSource? _firstFrame;
    private string _streamUrl;
    private long _sequence;
    private int _videoWidth;
    private int _videoHeight;
    private int _capturePending;
    private int _sessionVersion;
    private long _reportedDecoderOutputBuffers;
    private long _nextDecoderReportAt;
    private Bitmap? _captureBitmap;
    private volatile bool _running;
    private bool _disposed;

    public AndroidHlsFrameSource(
        Context context,
        AndroidVideoTextureView preview,
        string streamUrl,
        Func<int> recognitionFramesPerSecond,
        Func<bool> hasPendingRecognitionFrame,
        Func<Yuv420Frame, bool> submitFrame)
    {
        _context = context;
        _preview = preview;
        _streamUrl = streamUrl;
        _recognitionFramesPerSecond = recognitionFramesPerSecond;
        _hasPendingRecognitionFrame = hasPendingRecognitionFrame;
        _submitFrame = submitFrame;
        _metadataListener = new AndroidHlsVideoMetadataListener(OnVideoFrameDecoded);
        _playerErrorListener = new AndroidHlsPlayerErrorListener(OnPlayerError);
        _preview.SurfaceTextureListener = this;
    }

    public event EventHandler<string>? Diagnostic;
    public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
    public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented;
    public bool ReportsPreviewFrames => true;
    public bool IsReady => NetworkVideoStream.TryParse(_streamUrl, out _);

    public void SetNetworkStreamUrl(string value) => _streamUrl = value;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!NetworkVideoStream.TryParse(_streamUrl, out var stream))
        {
            throw new InvalidOperationException("Enter a valid HTTP or HTTPS .m3u8 URL before starting the stream.");
        }

        _recognitionFrameGate.Reset();
        _latencyPolicy.Reset();
        Interlocked.Increment(ref _sessionVersion);
        _firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running = true;
        await MainThread.InvokeOnMainThreadAsync(() => StartPlayer(stream!.Uri));

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartupTimeout);
            await _firstFrame.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            Diagnostic?.Invoke(this, "OME LL-HLS stream active · Media3 hardware decoding");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var playerError = _player?.PlayerError?.Message;
            await StopAsync().ConfigureAwait(false);
            throw new TimeoutException(
                playerError is null
                    ? $"The LL-HLS stream did not present a frame within {StartupTimeout.TotalSeconds:0} seconds."
                    : $"The LL-HLS stream failed before presenting a frame: {playerError}",
                exception);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void StartPlayer(Uri uri)
    {
        var player = EnsurePlayer();
        _reportedDecoderOutputBuffers = GetDecoderOutputBufferCount(player);
        _nextDecoderReportAt = Environment.TickCount64 + 1000;
        player.SetMediaItem(MediaItem.FromUri(uri.AbsoluteUri));
        player.Prepare();
        player.PlayWhenReady = true;
    }

    private IExoPlayer EnsurePlayer()
    {
        if (_player is not null)
        {
            return _player;
        }

        var player = new ExoPlayerBuilder(_context).Build()
            ?? throw new InvalidOperationException("Media3 could not create an ExoPlayer instance.");
        player.SetVideoFrameMetadataListener(_metadataListener);
        player.AddListener(_playerErrorListener);
        if (_surface is not null)
        {
            player.SetVideoSurface(_surface);
        }
        _player = player;
        return player;
    }

    public Task StopAsync()
    {
        _running = false;
        Interlocked.Increment(ref _sessionVersion);
        _recognitionFrameGate.Reset();
        _latencyPolicy.Reset();
        _firstFrame?.TrySetCanceled();
        _firstFrame = null;
        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            _player?.Stop();
            _player?.ClearMediaItems();
        });
    }

    private void OnVideoFrameDecoded(Media3Format format)
    {
        if (format.Width <= 0 || format.Height <= 0)
        {
            return;
        }

        var width = format.Width;
        var height = format.Height;
        var oldWidth = Volatile.Read(ref _videoWidth);
        var oldHeight = Volatile.Read(ref _videoHeight);
        if (oldWidth == width && oldHeight == height)
        {
            return;
        }

        Volatile.Write(ref _videoWidth, width);
        Volatile.Write(ref _videoHeight, height);
        _preview.Post(new Java.Lang.Runnable(() =>
        {
            _preview.SetVideoAspectRatio((float)width / height);
            _preview.SurfaceTexture?.SetDefaultBufferSize(width, height);
        }));
    }

    public void OnSurfaceTextureAvailable(SurfaceTexture surfaceTexture, int width, int height)
    {
        _surface?.Dispose();
        _surface = new Surface(surfaceTexture);
        _player?.SetVideoSurface(_surface);
    }

    public bool OnSurfaceTextureDestroyed(SurfaceTexture surfaceTexture)
    {
        if (_player is not null && _surface is not null)
        {
            _player.ClearVideoSurface(_surface);
        }
        _surface?.Dispose();
        _surface = null;
        return true;
    }

    public void OnSurfaceTextureSizeChanged(SurfaceTexture surfaceTexture, int width, int height)
    {
    }

    public void OnSurfaceTextureUpdated(SurfaceTexture surfaceTexture)
    {
        if (!_running)
        {
            return;
        }

        ReportDecodedFramesIfDue();
        RecoverLiveEdgeIfNeeded();
        PreviewFramesPresented?.Invoke(this, new DriveFrameCountEventArgs(1));
        _firstFrame?.TrySetResult();
        if (!_recognitionFrameGate.TryAcquire(
                Environment.TickCount64,
                _recognitionFramesPerSecond(),
                !_hasPendingRecognitionFrame())
            || Interlocked.CompareExchange(ref _capturePending, 1, 0) != 0)
        {
            return;
        }

        var width = Volatile.Read(ref _videoWidth);
        var height = Volatile.Read(ref _videoHeight);
        if (width <= 0 || height <= 0)
        {
            Volatile.Write(ref _capturePending, 0);
            return;
        }

        var pixels = ArrayPool<int>.Shared.Rent(checked(width * height));
        try
        {
            if (_captureBitmap is null || _captureBitmap.Width != width || _captureBitmap.Height != height)
            {
                _captureBitmap?.Dispose();
                _captureBitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)
                    ?? throw new InvalidOperationException("A decoded-frame capture bitmap could not be created.");
            }
            _preview.GetBitmap(_captureBitmap);
            _captureBitmap.GetPixels(pixels, 0, width, 0, 0, width, height);
        }
        catch (Exception exception)
        {
            ArrayPool<int>.Shared.Return(pixels);
            Volatile.Write(ref _capturePending, 0);
            Diagnostic?.Invoke(this, $"Decoded frame capture failed: {exception.Message}");
            return;
        }
        var sessionVersion = Volatile.Read(ref _sessionVersion);
        _ = Task.Run(() => ConvertAndSubmit(pixels, width, height, sessionVersion));
    }

    private void ReportDecodedFramesIfDue()
    {
        var now = Environment.TickCount64;
        if (_player is null || now < _nextDecoderReportAt)
        {
            return;
        }
        _nextDecoderReportAt = now + 1000;

        var current = GetDecoderOutputBufferCount(_player);
        var previous = _reportedDecoderOutputBuffers;
        _reportedDecoderOutputBuffers = current;
        if (current > previous)
        {
            SourceFramesAvailable?.Invoke(this, new DriveFrameCountEventArgs(current - previous));
        }
    }

    private void RecoverLiveEdgeIfNeeded()
    {
        var player = _player;
        if (player is null)
        {
            return;
        }

        var offsetMilliseconds = player.CurrentLiveOffset;
        if (offsetMilliseconds > 0
            && _latencyPolicy.ShouldResync(TimeSpan.FromMilliseconds(offsetMilliseconds), DateTimeOffset.UtcNow))
        {
            player.SeekToDefaultPosition();
            Diagnostic?.Invoke(this, "OME LL-HLS stream rejoined the live edge");
        }
    }

    private static long GetDecoderOutputBufferCount(IExoPlayer player)
    {
        var counters = player.VideoDecoderCounters;
        if (counters is null)
        {
            return 0;
        }

        counters.EnsureUpdated();
        return (long)counters.RenderedOutputBufferCount
            + counters.DroppedBufferCount
            + counters.SkippedOutputBufferCount;
    }

    private void OnPlayerError(PlaybackException exception)
    {
        if (!_running)
        {
            return;
        }

        var failure = new InvalidOperationException($"Media3 could not continue the LL-HLS stream: {exception.Message}", exception);
        _firstFrame?.TrySetException(failure);
        Diagnostic?.Invoke(this, $"OME LL-HLS playback failed: {exception.Message}");
    }

    private void ConvertAndSubmit(int[] pixels, int width, int height, int sessionVersion)
    {
        try
        {
            if (!_running || sessionVersion != Volatile.Read(ref _sessionVersion))
            {
                return;
            }

            var frame = ArgbFrameFactory.Create(
                pixels.AsSpan(0, checked(width * height)),
                width,
                height,
                Interlocked.Increment(ref _sequence),
                DateTimeOffset.UtcNow);
            try
            {
                if (!_running || sessionVersion != Volatile.Read(ref _sessionVersion))
                {
                    frame.Dispose();
                }
                else
                {
                    _submitFrame(frame);
                }
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke(this, $"Decoded frame conversion failed: {exception.Message}");
        }
        finally
        {
            ArrayPool<int>.Shared.Return(pixels);
            Volatile.Write(ref _capturePending, 0);
        }
    }

    public new void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _running = false;
        _preview.SurfaceTextureListener = null;
        if (_player is not null)
        {
            _player.RemoveListener(_playerErrorListener);
            _player.ClearVideoFrameMetadataListener(_metadataListener);
            _player.Release();
            _player.Dispose();
            _player = null;
        }
        _surface?.Dispose();
        _surface = null;
        _captureBitmap?.Dispose();
        _captureBitmap = null;
        base.Dispose();
    }

}
