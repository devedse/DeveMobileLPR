using AVFoundation;
using CoreAnimation;
using CoreFoundation;
using CoreMedia;
using CoreVideo;
using DeveMobileLPR.Application;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Streaming;
using Foundation;
using UIKit;

namespace DeveMobileLPR.App;

internal sealed class IosDriveFrameSource : IDriveVideoInput
{
    private readonly IosCameraPreviewView _preview;
    private readonly Func<int> _recognitionFramesPerSecond;
    private readonly Func<bool> _hasPendingFrame;
    private readonly Action<Yuv420Frame> _onFrame;
    private readonly FrameRateGate _gate = new(timestampFrequency: 1000);
    private readonly AVCaptureSession _session = new();
    private readonly AVCaptureVideoDataOutput _output = new();
    private readonly DispatchQueue _queue = new("nl.deve.mobilelpr.camera");
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly SampleDelegate _delegate;
    private readonly NSObject _orientationObserver;
    private AVCaptureDeviceInput? _input;
    private AVCaptureDevice? _device;
    private AVPlayer? _networkPlayer;
    private AVPlayerItem? _networkItem;
    private AVPlayerItemVideoOutput? _networkOutput;
    private CancellationTokenSource? _networkCancellation;
    private Task? _networkWorker;
    private long _sequence;
    private bool _initialized;
    private bool _running;
    private bool _disposed;
    private string _selectedCameraId = "rear";
    private string _networkStreamUrl;
    private float _requestedZoom = 1;
    private IReadOnlyList<CameraChoice> _choices = [new("rear", "Rear camera")];

    public IosDriveFrameSource(
        IosCameraPreviewView preview,
        Func<int> recognitionFramesPerSecond,
        Func<bool> hasPendingFrame,
        Action<Yuv420Frame> onFrame,
        string networkStreamUrl)
    {
        _preview = preview;
        _recognitionFramesPerSecond = recognitionFramesPerSecond;
        _hasPendingFrame = hasPendingFrame;
        _onFrame = onFrame;
        _networkStreamUrl = networkStreamUrl;
        _delegate = new SampleDelegate(this);
        UIDevice.CurrentDevice.BeginGeneratingDeviceOrientationNotifications();
        _orientationObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            UIDevice.OrientationDidChangeNotification,
            _ => MainThread.BeginInvokeOnMainThread(ApplyCaptureOrientation));
    }

    public event EventHandler<DriveInputDiagnostic>? Diagnostic;
    public event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged;
    public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
    public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented;
    public IReadOnlyList<CameraChoice> CameraChoices => _choices;
    public string SelectedCameraId => _selectedCameraId;
    public bool IsReady => _selectedCameraId == DriveInputIds.NetworkLlHls
        ? NetworkVideoStream.TryParse(_networkStreamUrl, out _)
        : _initialized;
    public bool SupportsNetworkStreams => true;
    public bool ReportsPreviewFrames => _selectedCameraId == DriveInputIds.NetworkLlHls;

    public async Task InitializeAsync(string preferredCameraId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _choices = AvailableChoices();
        _selectedCameraId = _choices.Any(choice => choice.Id == preferredCameraId)
            ? preferredCameraId
            : _choices[0].Id;
        if (_selectedCameraId != DriveInputIds.NetworkLlHls)
        {
            await EnsureCameraPermissionAsync(cancellationToken);
            await ConfigureSessionOnMainThreadAsync();
        }
        _initialized = true;
        CameraChoicesChanged?.Invoke(this, _choices);
        Report(_selectedCameraId == DriveInputIds.NetworkLlHls
            ? NetworkVideoStream.TryParse(_networkStreamUrl, out _)
                ? "OME LL-HLS stream ready"
                : "Enter an HTTP or HTTPS .m3u8 URL for the OME LL-HLS stream."
            : "iPhone camera ready · AVFoundation NV12 capture");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("The camera has not been initialized.");
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_running) return;
            _gate.Reset();
            if (_selectedCameraId == DriveInputIds.NetworkLlHls)
            {
                await StartNetworkAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_session.Running) _session.StartRunning();
                }, cancellationToken).ConfigureAwait(false);
            }
            _running = true;
            Report("Video input active · recognition stays on this iPhone");
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_running) return;
            _running = false;
            _gate.Reset();
            if (_selectedCameraId == DriveInputIds.NetworkLlHls)
            {
                await StopNetworkAsync().ConfigureAwait(false);
            }
            else
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_session.Running) _session.StopRunning();
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async Task SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_choices.Any(choice => choice.Id == cameraId))
        {
            throw new ArgumentException("The selected iPhone video input is unavailable.", nameof(cameraId));
        }
        if (cameraId == _selectedCameraId) return;

        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var restart = _running;
            var previousCameraId = _selectedCameraId;
            if (restart)
            {
                _running = false;
                if (_selectedCameraId == DriveInputIds.NetworkLlHls) await StopNetworkAsync().ConfigureAwait(false);
                else if (_session.Running) _session.StopRunning();
            }

            try
            {
                _selectedCameraId = cameraId;
                if (cameraId != DriveInputIds.NetworkLlHls)
                {
                    await EnsureCameraPermissionAsync(cancellationToken);
                    await ConfigureSessionOnMainThreadAsync();
                }

                if (restart)
                {
                    if (cameraId == DriveInputIds.NetworkLlHls) await StartNetworkAsync(cancellationToken).ConfigureAwait(false);
                    else _session.StartRunning();
                    _running = true;
                }
            }
            catch (Exception switchException)
            {
                _selectedCameraId = previousCameraId;
                try
                {
                    if (previousCameraId != DriveInputIds.NetworkLlHls)
                    {
                        await ConfigureSessionOnMainThreadAsync();
                    }
                    if (restart)
                    {
                        if (previousCameraId == DriveInputIds.NetworkLlHls) await StartNetworkAsync(CancellationToken.None).ConfigureAwait(false);
                        else _session.StartRunning();
                        _running = true;
                    }
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "The selected input could not start and the previous input could not be resumed.",
                        switchException,
                        rollbackException);
                }
                throw;
            }
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public void SetZoom(float zoomRatio)
    {
        _requestedZoom = Math.Max(1, zoomRatio);
        ApplyZoom();
    }

    public void SetNetworkStreamUrl(string value)
    {
        _networkStreamUrl = value;
        if (_selectedCameraId == DriveInputIds.NetworkLlHls)
        {
            Report(NetworkVideoStream.TryParse(value, out _)
                ? "OME LL-HLS stream ready"
                : "Enter an HTTP or HTTPS .m3u8 URL for the OME LL-HLS stream.");
        }
    }

    private static async Task EnsureCameraPermissionAsync(CancellationToken cancellationToken)
    {
        var permission = await Permissions.RequestAsync<Permissions.Camera>();
        cancellationToken.ThrowIfCancellationRequested();
        if (permission != PermissionStatus.Granted)
        {
            throw new UnauthorizedAccessException(
                "Camera access is required to recognize plates. You can enable it in iPhone settings.");
        }
    }

    private async Task StartNetworkAsync(CancellationToken cancellationToken)
    {
        if (!NetworkVideoStream.TryParse(_networkStreamUrl, out var stream) || stream is null)
        {
            throw new InvalidOperationException(
                "Enter an HTTP or HTTPS .m3u8 URL for the OME LL-HLS stream.");
        }

        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            using var url = NSUrl.FromString(stream.Uri.AbsoluteUri)
                ?? throw new InvalidOperationException("The LL-HLS URL could not be represented by iOS.");
            var item = AVPlayerItem.FromUrl(url);
            var output = new AVPlayerItemVideoOutput(new CVPixelBufferAttributes
            {
                PixelFormatType = CVPixelFormatType.CV420YpCbCr8BiPlanarFullRange
            });
            item.AddOutput(output);
            var player = AVPlayer.FromPlayerItem(item);
            _networkItem = item;
            _networkOutput = output;
            _networkPlayer = player;
            _preview.Attach(player);
            player.Play();
        });

        _networkCancellation = new CancellationTokenSource();
        _networkWorker = Task.Run(
            () => PollNetworkFramesAsync(firstFrame, _networkCancellation.Token),
            CancellationToken.None);
        try
        {
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            Report("OME LL-HLS active · AVFoundation NV12 decode");
        }
        catch
        {
            await StopNetworkAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task PollNetworkFramesAsync(
        TaskCompletionSource firstFrame,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var output = _networkOutput;
                if (output is not null)
                {
                    var itemTime = output.GetItemTime(CAAnimation.CurrentMediaTime());
                    if (output.HasNewPixelBufferForItemTime(itemTime))
                    {
                        var displayTime = CMTime.Invalid;
                        using var pixelBuffer = output.CopyPixelBuffer(itemTime, ref displayTime);
                        if (pixelBuffer is not null)
                        {
                            firstFrame.TrySetResult();
                            SourceFramesAvailable?.Invoke(this, new DriveFrameCountEventArgs(1));
                            PreviewFramesPresented?.Invoke(this, new DriveFrameCountEventArgs(1));
                            if (!_hasPendingFrame()
                                && _gate.TryAcquire(Environment.TickCount64, _recognitionFramesPerSecond()))
                            {
                                SubmitPixelBuffer(pixelBuffer);
                            }
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            firstFrame.TrySetException(exception);
            Report($"iPhone LL-HLS ingestion failed: {exception.Message}", true);
        }
    }

    private async Task StopNetworkAsync()
    {
        var cancellation = Interlocked.Exchange(ref _networkCancellation, null);
        var worker = Interlocked.Exchange(ref _networkWorker, null);
        cancellation?.Cancel();
        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        cancellation?.Dispose();

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _networkPlayer?.Pause();
            if (_networkItem is not null && _networkOutput is not null)
            {
                _networkItem.RemoveOutput(_networkOutput);
            }
            _networkOutput?.Dispose();
            _networkItem?.Dispose();
            _networkPlayer?.Dispose();
            _networkOutput = null;
            _networkItem = null;
            _networkPlayer = null;
        });
    }

    private Task ConfigureSessionOnMainThreadAsync() =>
        MainThread.InvokeOnMainThreadAsync(ConfigureSession);

    private void ConfigureSession()
    {
        _session.BeginConfiguration();
        try
        {
            if (_input is not null) _session.RemoveInput(_input);
            if (_session.Outputs.Contains(_output)) _session.RemoveOutput(_output);
            _input?.Dispose();
            _device?.Dispose();

            var position = _selectedCameraId == "front"
                ? AVCaptureDevicePosition.Front
                : AVCaptureDevicePosition.Back;
            _device = AVCaptureDevice.GetDefaultDevice(
                AVCaptureDeviceType.BuiltInWideAngleCamera,
                AVMediaTypes.Video,
                position) ?? throw new InvalidOperationException("The selected iPhone camera is unavailable.");
            _input = AVCaptureDeviceInput.FromDevice(_device, out var error);
            if (_input is null) throw new InvalidOperationException(error?.LocalizedDescription ?? "The camera input could not be created.");
            if (!_session.CanAddInput(_input)) throw new InvalidOperationException("The camera input cannot be attached.");
            _session.AddInput(_input);

            _output.AlwaysDiscardsLateVideoFrames = true;
            _output.WeakVideoSettings = new CVPixelBufferAttributes
            {
                PixelFormatType = CVPixelFormatType.CV420YpCbCr8BiPlanarFullRange
            }.Dictionary;
            _output.SetSampleBufferDelegate(_delegate, _queue);
            if (!_session.CanAddOutput(_output)) throw new InvalidOperationException("The camera frame output cannot be attached.");
            _session.AddOutput(_output);
            ApplyCaptureOrientation();
            _preview.Attach(_session);
            ApplyZoom();
        }
        finally
        {
            _session.CommitConfiguration();
        }
    }

    private static IReadOnlyList<CameraChoice> AvailableChoices()
    {
        var choices = new List<CameraChoice>();
        if (AVCaptureDevice.GetDefaultDevice(AVCaptureDeviceType.BuiltInWideAngleCamera, AVMediaTypes.Video, AVCaptureDevicePosition.Back) is { } rear)
        {
            choices.Add(new CameraChoice("rear", "Rear camera"));
            rear.Dispose();
        }
        if (AVCaptureDevice.GetDefaultDevice(AVCaptureDeviceType.BuiltInWideAngleCamera, AVMediaTypes.Video, AVCaptureDevicePosition.Front) is { } front)
        {
            choices.Add(new CameraChoice("front", "Front camera"));
            front.Dispose();
        }
        choices.Add(new CameraChoice(DriveInputIds.NetworkLlHls, "OME LL-HLS stream"));
        return choices;
    }

    private void ApplyCaptureOrientation()
    {
#pragma warning disable CA1422 // Required for the iOS 16 baseline; keeps data output aligned with the preview.
        var connection = _output.ConnectionFromMediaType(AVMediaTypes.Video.GetConstant()!);
        if (connection?.SupportsVideoOrientation != true)
        {
            return;
        }

        var interfaceOrientation = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .FirstOrDefault(scene => scene.ActivationState == UISceneActivationState.ForegroundActive)
            ?.InterfaceOrientation ?? UIInterfaceOrientation.Portrait;
        connection.VideoOrientation = interfaceOrientation switch
        {
            UIInterfaceOrientation.LandscapeLeft => AVCaptureVideoOrientation.LandscapeLeft,
            UIInterfaceOrientation.LandscapeRight => AVCaptureVideoOrientation.LandscapeRight,
            UIInterfaceOrientation.PortraitUpsideDown => AVCaptureVideoOrientation.PortraitUpsideDown,
            _ => AVCaptureVideoOrientation.Portrait
        };
#pragma warning restore CA1422
    }

    private void ApplyZoom()
    {
        if (_device is null) return;
        if (!_device.LockForConfiguration(out var error))
        {
            Report(error?.LocalizedDescription ?? "Camera zoom could not be configured.", true);
            return;
        }
        try
        {
            var maximum = (float)Math.Min(_device.ActiveFormat.VideoMaxZoomFactor, 4);
            _device.VideoZoomFactor = Math.Clamp(_requestedZoom, 1, maximum);
        }
        finally
        {
            _device.UnlockForConfiguration();
        }
    }

    private unsafe void Receive(CMSampleBuffer sampleBuffer)
    {
        if (!_running) return;
        SourceFramesAvailable?.Invoke(this, new DriveFrameCountEventArgs(1));
        if (_hasPendingFrame() || !_gate.TryAcquire(Environment.TickCount64, _recognitionFramesPerSecond())) return;
        using var pixelBuffer = sampleBuffer.GetImageBuffer() as CVPixelBuffer;
        if (pixelBuffer is null || pixelBuffer.PlaneCount != 2) return;
        SubmitPixelBuffer(pixelBuffer);
    }

    private unsafe void SubmitPixelBuffer(CVPixelBuffer pixelBuffer)
    {
        if (pixelBuffer.PlaneCount != 2) return;
        pixelBuffer.Lock(CVPixelBufferLock.ReadOnly);
        try
        {
            var width = checked((int)pixelBuffer.Width);
            var height = checked((int)pixelBuffer.Height);
            var yStride = checked((int)pixelBuffer.GetBytesPerRowOfPlane(0));
            var uvStride = checked((int)pixelBuffer.GetBytesPerRowOfPlane(1));
            var y = new ReadOnlySpan<byte>((void*)pixelBuffer.GetBaseAddress(0), checked(yStride * height));
            var uvHeight = (height + 1) / 2;
            var uv = new ReadOnlySpan<byte>((void*)pixelBuffer.GetBaseAddress(1), checked(uvStride * uvHeight));
            _onFrame(BiPlanarNv12FrameFactory.Create(
                y, yStride, uv, uvStride, width, height,
                Interlocked.Increment(ref _sequence), DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            Report($"iPhone frame ingestion failed: {exception.Message}", true);
        }
        finally
        {
            pixelBuffer.Unlock(CVPixelBufferLock.ReadOnly);
        }
    }

    private void Report(string message, bool error = false) =>
        Diagnostic?.Invoke(this, new DriveInputDiagnostic(message, error));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _output.SetSampleBufferDelegate(null, null);
        NSNotificationCenter.DefaultCenter.RemoveObserver(_orientationObserver);
        _orientationObserver.Dispose();
        UIDevice.CurrentDevice.EndGeneratingDeviceOrientationNotifications();
        _input?.Dispose();
        _device?.Dispose();
        _delegate.Dispose();
        _output.Dispose();
        _session.Dispose();
        _queue.Dispose();
        _switchGate.Dispose();
    }

    private sealed class SampleDelegate(IosDriveFrameSource owner) : AVCaptureVideoDataOutputSampleBufferDelegate
    {
        public override void DidOutputSampleBuffer(
            AVCaptureOutput captureOutput,
            CMSampleBuffer sampleBuffer,
            AVCaptureConnection connection) => owner.Receive(sampleBuffer);
    }
}
