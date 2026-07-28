using DeveMobileLPR.App.Services;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Streaming;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Devices.Enumeration;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;

namespace DeveMobileLPR.App.Platforms.Windows;

internal sealed class WindowsWebcamFrameSource : IAsyncDisposable
{
    private readonly MediaPlayerElement _preview;
    private readonly Microsoft.UI.Xaml.Controls.Image _streamPreview;
    private readonly SoftwareBitmapSource _streamPreviewSource = new();
    private readonly Func<Yuv420Frame, bool> _submitFrame;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _frameSignal = new(0, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _frameWorker;
    private MediaCapture? _capture;
    private MediaPlayer? _player;
    private MediaFrameSource? _frameSource;
    private MediaFrameReader? _reader;
    private SoftwareBitmap? _latestBitmap;
    private IReadOnlyList<CameraChoice> _cameraChoices = [];
    private string _selectedCameraId = string.Empty;
    private string _networkStreamUrl;
    private bool _analyzing;
    private bool _disposed;
    private long _sequence;
    private long _nextStreamFrameTicks;
    private int _streamFramePending;
    private TaskCompletionSource? _streamOpened;

    public WindowsWebcamFrameSource(
        MediaPlayerElement preview,
        Microsoft.UI.Xaml.Controls.Image streamPreview,
        string networkStreamUrl,
        Func<Yuv420Frame, bool> submitFrame)
    {
        _preview = preview;
        _streamPreview = streamPreview;
        _streamPreview.Source = _streamPreviewSource;
        _networkStreamUrl = networkStreamUrl;
        _submitFrame = submitFrame;
        _frameWorker = Task.Run(ProcessFramesAsync);
    }

    public event EventHandler<string>? Diagnostic;
    public event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged;
    public IReadOnlyList<CameraChoice> CameraChoices => _cameraChoices;
    public string SelectedCameraId => _selectedCameraId;
    public bool IsReady => _selectedCameraId == DriveInputIds.NetworkLlHls
        ? NetworkVideoStream.TryParse(_networkStreamUrl, out _)
        : _capture is not null;

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
            ObjectDisposedException.ThrowIf(_disposed, this);
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        cancellationToken.ThrowIfCancellationRequested();
        _cameraChoices = cameras.Select(static camera => new CameraChoice(camera.Id, camera.Name))
            .Append(new CameraChoice(DriveInputIds.NetworkLlHls, "OME LL-HLS stream"))
            .ToArray();
        CameraChoicesChanged?.Invoke(this, _cameraChoices);

        await StopReaderCoreAsync();
        ReleaseCapture();
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
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            VideoDeviceId = selected.Id,
            SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu
        });
        cancellationToken.ThrowIfCancellationRequested();

        var source = capture.FrameSources.Values.FirstOrDefault(static item =>
                item.Info.SourceKind == MediaFrameSourceKind.Color && item.Info.MediaStreamType == MediaStreamType.VideoPreview)
            ?? capture.FrameSources.Values.FirstOrDefault(static item =>
                item.Info.SourceKind == MediaFrameSourceKind.Color && item.Info.MediaStreamType == MediaStreamType.VideoRecord)
            ?? throw new InvalidOperationException("The selected webcam has no color video stream.");
        var player = new MediaPlayer
        {
            AutoPlay = true,
            RealTimePlayback = true,
            Source = MediaSource.CreateFromMediaFrameSource(source)
        };
        _preview.SetMediaPlayer(player);
        player.Play();
        _capture = capture;
        _frameSource = source;
        _player = player;
        _selectedCameraId = selected.Id;
        _preview.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        _streamPreview.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        Diagnostic?.Invoke(this, $"Camera ready · {selected.Name}");
    }

    private async Task StartWebcamReaderCoreAsync()
    {
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

        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new MediaPlayer
        {
            AutoPlay = false,
            IsVideoFrameServerEnabled = true,
            RealTimePlayback = true,
            Source = MediaSource.CreateFromUri(stream!.Uri)
        };
        player.MediaOpened += StreamOpened;
        player.MediaFailed += StreamFailed;
        player.VideoFrameAvailable += StreamFrameAvailable;
        _streamOpened = opened;
        _player = player;
        _analyzing = true;
        player.Play();
        try
        {
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            Diagnostic?.Invoke(this, "OME LL-HLS stream active · processing stays on this device");
        }
        catch
        {
            await StopReaderCoreAsync();
            throw;
        }
    }

    private void StreamOpened(MediaPlayer sender, object args) => _streamOpened?.TrySetResult();

    private void StreamFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        _streamOpened?.TrySetException(new InvalidDataException($"OME LL-HLS playback failed: {args.ErrorMessage}"));

    private async void StreamFrameAvailable(MediaPlayer sender, object args)
    {
        var now = Environment.TickCount64;
        if (now < Interlocked.Read(ref _nextStreamFrameTicks)
            || Interlocked.CompareExchange(ref _streamFramePending, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _nextStreamFrameTicks, now + 250);
        try
        {
            var width = checked((int)sender.PlaybackSession.NaturalVideoWidth);
            var height = checked((int)sender.PlaybackSession.NaturalVideoHeight);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            using var gpuFrame = VideoFrame.CreateAsDirect3D11SurfaceBacked(
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                width,
                height);
            sender.CopyFrameToVideoSurface(gpuFrame.Direct3DSurface);
            var owned = await SoftwareBitmap.CreateCopyFromSurfaceAsync(gpuFrame.Direct3DSurface);
            Interlocked.Exchange(ref _latestBitmap, owned)?.Dispose();
            if (_frameSignal.CurrentCount == 0)
            {
                try { _frameSignal.Release(); } catch (SemaphoreFullException) { }
            }
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke(this, $"OME stream frame processing failed: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _streamFramePending, 0);
        }
    }

    private void FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var reference = sender.TryAcquireLatestFrame();
        var bitmap = reference?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null)
        {
            return;
        }

        var owned = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? SoftwareBitmap.Copy(bitmap)
            : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        Interlocked.Exchange(ref _latestBitmap, owned)?.Dispose();
        if (_frameSignal.CurrentCount == 0)
        {
            try { _frameSignal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private async Task ProcessFramesAsync()
    {
        try
        {
            while (true)
            {
                await _frameSignal.WaitAsync(_cancellation.Token).ConfigureAwait(false);
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
                if (_selectedCameraId == DriveInputIds.NetworkLlHls)
                {
                    await UpdateStreamPreviewAsync(bitmap).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke(this, $"Camera frame processing failed: {exception.Message}");
        }
    }

    private async Task StopReaderCoreAsync()
    {
        _analyzing = false;
        _streamOpened = null;
        if (_reader is null)
        {
            ReleasePlayer();
            Interlocked.Exchange(ref _latestBitmap, null)?.Dispose();
            return;
        }

        _reader.FrameArrived -= FrameArrived;
        await _reader.StopAsync();
        _reader.Dispose();
        _reader = null;
        Interlocked.Exchange(ref _latestBitmap, null)?.Dispose();
    }

    private async Task UpdateStreamPreviewAsync(SoftwareBitmap bitmap)
    {
        using var previewBitmap = SoftwareBitmap.Copy(bitmap);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_streamPreview.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await _streamPreviewSource.SetBitmapAsync(previewBitmap);
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            return;
        }
        await completion.Task.ConfigureAwait(false);
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

        _player.MediaOpened -= StreamOpened;
        _player.MediaFailed -= StreamFailed;
        _player.VideoFrameAvailable -= StreamFrameAvailable;
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
        }
        finally
        {
            _lifecycleGate.Release();
        }
        try { await _frameWorker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        Interlocked.Exchange(ref _latestBitmap, null)?.Dispose();
        _cancellation.Dispose();
        _frameSignal.Dispose();
        _lifecycleGate.Dispose();
    }
}