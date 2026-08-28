using System.Diagnostics;
using System.Threading.Channels;
using DeveMobileLPR.Application;
using DeveMobileLPR.Imaging;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;

namespace DeveMobileLPR.App.Platforms.Windows.Camera;

/// <summary>
/// Owns physical Windows webcam preview, BGRA frame acquisition, and conversion. Device discovery
/// is supplied by the platform source catalog so capability mapping has one source of truth.
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
    private CameraChoice[] _cameras = [];
    private float _requestedZoomRatio = 1f;
    private float? _lastAppliedZoomRatio;
    private bool _zoomUnsupportedReported;
    private DriveZoomState _zoomState = DriveZoomState.Pending(1f);
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
    public event EventHandler<DriveZoomState>? ZoomStateChanged;
    public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented
    {
        add { }
        remove { }
    }

    public bool ReportsPreviewFrames => false;
    public bool IsReady => _capture is not null;
    public string SelectedCameraId { get; private set; } = string.Empty;
    public DriveZoomState ZoomState => _zoomState;

    public void ConfigureCameraChoices(IReadOnlyList<CameraChoice> cameras)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cameras);
        _cameras = cameras.ToArray();
    }

    public async Task InitializeAsync(string preferredCameraId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
                // Camera controls such as zoom cannot be changed in SharedReadOnly mode.
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
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
            _lastAppliedZoomRatio = null;
            _zoomUnsupportedReported = false;
            if (IsPreviewActive)
            {
                _preview.SetMediaPlayer(player);
                player.Play();
                ApplyRequestedZoom();
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

        ApplyRequestedZoom();
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

    public void SetZoom(float zoomRatio)
    {
        var requested = Math.Clamp(zoomRatio, 1f, 4f);
        Volatile.Write(ref _requestedZoomRatio, requested);
        SetZoomState(DriveZoomState.Pending(requested));
        ApplyRequestedZoom();
    }

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
            ApplyRequestedZoom();
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
        _lastAppliedZoomRatio = null;
        _zoomUnsupportedReported = false;
    }

    private void ApplyRequestedZoom()
    {
        var capture = _capture;
        if (capture is null)
        {
            return;
        }

        try
        {
            var control = capture.VideoDeviceController.ZoomControl;
            if (!control.Supported)
            {
                SetZoomState(DriveZoomState.Unavailable(Volatile.Read(ref _requestedZoomRatio)));
                if (!_zoomUnsupportedReported)
                {
                    _zoomUnsupportedReported = true;
                    Diagnostic?.Invoke(this, new DriveInputDiagnostic(
                        "Camera zoom is not supported by this webcam."));
                }
                return;
            }

            var requested = Volatile.Read(ref _requestedZoomRatio);
            var target = Math.Clamp(requested, control.Min, control.Max);
            if (control.Step > 0)
            {
                var steps = MathF.Round((target - control.Min) / control.Step);
                target = Math.Clamp(control.Min + (steps * control.Step), control.Min, control.Max);
            }

            var mode = control.SupportedModes.Contains(ZoomTransitionMode.Direct)
                ? ZoomTransitionMode.Direct
                : ZoomTransitionMode.Auto;
            control.Configure(new ZoomSettings
            {
                Mode = mode,
                Value = target
            });
            SetZoomState(new DriveZoomState(
                DriveZoomKind.CameraManaged,
                requested,
                target,
                1f,
                control.Max));

            if (_lastAppliedZoomRatio is null || Math.Abs(_lastAppliedZoomRatio.Value - target) > 0.001f)
            {
                _lastAppliedZoomRatio = target;
                Diagnostic?.Invoke(this, new DriveInputDiagnostic(
                    $"Camera zoom applied: {target:0.0}× " +
                    $"(requested {requested:0.0}×; supported {control.Min:0.0}–{control.Max:0.0}×)."));
            }
        }
        catch (Exception exception)
        {
            SetZoomState(DriveZoomState.Unavailable(Volatile.Read(ref _requestedZoomRatio)));
            Debug.WriteLine($"Could not apply Windows webcam zoom: {exception}");
            Diagnostic?.Invoke(this, new DriveInputDiagnostic(
                $"Camera zoom could not be applied: {exception.Message}",
                true));
        }
    }

    private void SetZoomState(DriveZoomState state)
    {
        _zoomState = state;
        ZoomStateChanged?.Invoke(this, state);
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
