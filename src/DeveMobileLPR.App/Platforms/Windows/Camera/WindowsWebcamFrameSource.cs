using System.Diagnostics;
using System.Threading.Channels;
using DeveMobileLPR.Application;
using DeveMobileLPR.Imaging;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;

namespace DeveMobileLPR.App.Platforms.Windows.Camera;

/// <summary>
/// Owns physical Windows webcam discovery, preview, BGRA frame acquisition, and conversion.
/// Input selection between this source and LL-HLS belongs to <see cref="WindowsDriveVideoInput"/>.
/// </summary>
internal sealed class WindowsWebcamFrameSource : IAsyncDisposable, IDriveFrameSourceTelemetry
{
    private readonly MediaPlayerElement _preview;
    private readonly Func<int> _recognitionFramesPerSecond;
    private readonly Func<Yuv420Frame, bool> _submitFrame;
    private readonly Func<long> _nextSequence;
    private readonly FrameRateGate _recognitionFrameGate = new(timestampFrequency: 1000);
    private readonly Channel<byte> _frameSignal = CreateSignalChannel();
    private readonly Task _frameWorker;
    private MediaCapture? _capture;
    private MediaPlayer? _player;
    private MediaFrameSource? _frameSource;
    private MediaFrameReader? _reader;
    private SoftwareBitmap? _latestBitmap;
    private DeviceInformation[] _cameras = [];
    private bool _disposed;
    private int _previewDeactivated;
    private int _previewLoaded;

    public WindowsWebcamFrameSource(
        MediaPlayerElement preview,
        Func<int> recognitionFramesPerSecond,
        Func<Yuv420Frame, bool> submitFrame,
        Func<long> nextSequence)
    {
        _preview = preview;
        _recognitionFramesPerSecond = recognitionFramesPerSecond;
        _submitFrame = submitFrame;
        _nextSequence = nextSequence;
        _preview.Loaded += PreviewLoaded;
        _preview.Unloaded += PreviewUnloaded;
        _frameWorker = Task.Run(ProcessFramesAsync);
    }

    public event EventHandler<DriveInputDiagnostic>? Diagnostic;
    public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
    public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented
    {
        add { }
        remove { }
    }

    public bool ReportsPreviewFrames => false;
    public bool IsReady => _capture is not null;
    public string SelectedCameraId { get; private set; } = string.Empty;

    public async Task<IReadOnlyList<CameraChoice>> RefreshCameraChoicesAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        cancellationToken.ThrowIfCancellationRequested();
        _cameras = cameras.ToArray();
        return _cameras.Select(static camera => new CameraChoice(camera.Id, camera.Name)).ToArray();
    }

    public async Task InitializeAsync(string preferredCameraId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cameras.Length == 0)
        {
            await RefreshCameraChoicesAsync(cancellationToken);
        }
        if (_cameras.Length == 0)
        {
            throw new InvalidOperationException("No Windows webcam is available.");
        }

        await ResetAsync();
        var selected = _cameras.FirstOrDefault(camera =>
                string.Equals(camera.Id, preferredCameraId, StringComparison.Ordinal))
            ?? _cameras[0];
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
            ObjectDisposedException.ThrowIf(_disposed, this);

            var source = capture.FrameSources.Values.FirstOrDefault(static item =>
                    item.Info.SourceKind == MediaFrameSourceKind.Color
                    && item.Info.MediaStreamType == MediaStreamType.VideoPreview)
                ?? capture.FrameSources.Values.FirstOrDefault(static item =>
                    item.Info.SourceKind == MediaFrameSourceKind.Color
                    && item.Info.MediaStreamType == MediaStreamType.VideoRecord)
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
            SelectedCameraId = selected.Id;
            if (IsPreviewActive)
            {
                _preview.SetMediaPlayer(player);
                player.Play();
            }
            Diagnostic?.Invoke(this, new DriveInputDiagnostic($"Camera ready · {selected.Name}"));
        }
        catch
        {
            if (ReferenceEquals(_capture, capture))
            {
                _capture = null;
                _frameSource = null;
                _player = null;
                SelectedCameraId = string.Empty;
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

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_capture is null || _frameSource is null)
        {
            throw new InvalidOperationException("No Windows webcam is ready.");
        }
        if (_reader is not null)
        {
            return;
        }

        _recognitionFrameGate.Reset();
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

        Diagnostic?.Invoke(this, new DriveInputDiagnostic("Camera active · processing stays on this device"));
    }

    public async Task StopAsync()
    {
        _recognitionFrameGate.Reset();
        if (_reader is not null)
        {
            _reader.FrameArrived -= FrameArrived;
            await _reader.StopAsync();
            _reader.Dispose();
            _reader = null;
        }
        Interlocked.Exchange(ref _latestBitmap, null)?.Dispose();
    }

    public async Task ResetAsync()
    {
        await StopAsync();
        ReleaseCapture();
        SelectedCameraId = string.Empty;
    }

    // A Windows zoom implementation has not yet been added to this adapter.
    public void SetZoom(float zoomRatio) { }

    public void DeactivatePreview()
    {
        if (Interlocked.Exchange(ref _previewDeactivated, 1) != 0)
        {
            return;
        }

        _preview.Loaded -= PreviewLoaded;
        _preview.Unloaded -= PreviewUnloaded;
        Volatile.Write(ref _previewLoaded, 0);
        _preview.SetMediaPlayer(null);
    }

    private static Channel<byte> CreateSignalChannel() => Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false
    });

    private bool IsPreviewActive => Volatile.Read(ref _previewLoaded) != 0
        && Volatile.Read(ref _previewDeactivated) == 0;

    private void PreviewLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs args)
    {
        if (Volatile.Read(ref _previewDeactivated) != 0)
        {
            return;
        }

        Volatile.Write(ref _previewLoaded, 1);
        if (_player is not null)
        {
            _preview.SetMediaPlayer(_player);
            _player.Play();
        }
    }

    private void PreviewUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs args)
    {
        Volatile.Write(ref _previewLoaded, 0);
        _preview.SetMediaPlayer(null);
    }

    private void FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var reference = sender.TryAcquireLatestFrame();
        var bitmap = reference?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null)
        {
            return;
        }

        SourceFramesAvailable?.Invoke(this, new DriveFrameCountEventArgs(1));
        if (!_recognitionFrameGate.TryAcquire(Environment.TickCount64, _recognitionFramesPerSecond()))
        {
            return;
        }

        var owned = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? SoftwareBitmap.Copy(bitmap)
            : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        Interlocked.Exchange(ref _latestBitmap, owned)?.Dispose();
        _frameSignal.Writer.TryWrite(0);
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
                    _nextSequence(),
                    DateTimeOffset.UtcNow);
                _submitFrame(frame);
            }
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke(this, new DriveInputDiagnostic($"Camera frame processing failed: {exception.Message}", true));
        }
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
        await StopAsync();
        DeactivatePreview();
        ReleaseCapture();
        _frameSignal.Writer.TryComplete();
        try
        {
            await _frameWorker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        Interlocked.Exchange(ref _latestBitmap, null)?.Dispose();
    }
}
