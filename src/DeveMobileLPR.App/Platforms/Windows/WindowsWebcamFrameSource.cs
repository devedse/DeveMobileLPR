using DeveMobileLPR.App.Services;
using DeveMobileLPR.Imaging;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Enumeration;
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
    private bool _analyzing;
    private bool _disposed;
    private long _sequence;

    public WindowsWebcamFrameSource(MediaPlayerElement preview, Func<Yuv420Frame, bool> submitFrame)
    {
        _preview = preview;
        _submitFrame = submitFrame;
        _frameWorker = Task.Run(ProcessFramesAsync);
    }

    public event EventHandler<string>? Diagnostic;
    public event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged;
    public IReadOnlyList<CameraChoice> CameraChoices => _cameraChoices;
    public string SelectedCameraId => _selectedCameraId;
    public bool IsReady => _capture is not null;

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
                throw new InvalidOperationException("No Windows webcam is ready.");
            }

            _reader = await _capture.CreateFrameReaderAsync(_frameSource, MediaEncodingSubtypes.Bgra8);
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
                _reader = await _capture!.CreateFrameReaderAsync(_frameSource!, MediaEncodingSubtypes.Bgra8);
                _reader.FrameArrived += FrameArrived;
                var status = await _reader.StartAsync();
                if (status != MediaFrameReaderStartStatus.Success)
                {
                    throw new InvalidOperationException($"The selected webcam could not start ({status}).");
                }
                _analyzing = true;
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
        _cameraChoices = cameras.Select(static camera => new CameraChoice(camera.Id, camera.Name)).ToArray();
        CameraChoicesChanged?.Invoke(this, _cameraChoices);
        if (_cameraChoices.Count == 0)
        {
            throw new InvalidOperationException("No Windows webcam was found.");
        }

        var selected = cameras.FirstOrDefault(camera => string.Equals(camera.Id, preferredCameraId, StringComparison.Ordinal)) ?? cameras[0];
        await StopReaderCoreAsync();
        ReleaseCapture();

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
        Diagnostic?.Invoke(this, $"Camera ready · {selected.Name}");
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
        if (_reader is null)
        {
            return;
        }

        _reader.FrameArrived -= FrameArrived;
        await _reader.StopAsync();
        _reader.Dispose();
        _reader = null;
        Interlocked.Exchange(ref _latestBitmap, null)?.Dispose();
    }

    private void ReleaseCapture()
    {
        _preview.SetMediaPlayer(null);
        _player?.Dispose();
        _player = null;
        _frameSource = null;
        _capture?.Dispose();
        _capture = null;
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