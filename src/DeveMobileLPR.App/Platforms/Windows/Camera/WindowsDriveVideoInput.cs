using DeveMobileLPR.Application;
using DeveMobileLPR.Imaging;
using Microsoft.UI.Xaml.Controls;

namespace DeveMobileLPR.App.Platforms.Windows.Camera;

/// <summary>
/// Selects between a physical Windows webcam and the Windows LL-HLS source. Each native source
/// owns its own capture, decoding, preview, and frame-delivery lifecycle.
/// </summary>
internal sealed class WindowsDriveVideoInput : IDriveVideoInput
{
    private readonly MediaPlayerElement _webcamPreview;
    private readonly Microsoft.UI.Xaml.Controls.Image _networkPreview;
    private readonly WindowsWebcamFrameSource _webcam;
    private readonly WindowsHlsFrameSource _network;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private IReadOnlyList<CameraChoice> _cameraChoices = [];
    private IReadOnlyList<DriveSourceCapability> _sourceCapabilities = [];
    private string _selectedCameraId = string.Empty;
    private bool _running;
    private bool _disposed;
    private int _previewDeactivated;
    private long _sequence;

    public WindowsDriveVideoInput(
        MediaPlayerElement webcamPreview,
        Microsoft.UI.Xaml.Controls.Image networkPreview,
        string networkStreamUrl,
        Func<int> recognitionFramesPerSecond,
        Func<bool> hasPendingRecognitionFrame,
        Func<Yuv420Frame, bool> submitFrame)
    {
        _webcamPreview = webcamPreview;
        _networkPreview = networkPreview;
        _webcam = new WindowsWebcamFrameSource(
            webcamPreview,
            recognitionFramesPerSecond,
            submitFrame,
            NextSequence);
        _network = new WindowsHlsFrameSource(
            networkPreview,
            networkStreamUrl,
            recognitionFramesPerSecond,
            hasPendingRecognitionFrame,
            submitFrame,
            NextSequence);
        _webcam.Diagnostic += ChildDiagnostic;
        _webcam.SourceFramesAvailable += ChildSourceFramesAvailable;
        _network.Diagnostic += ChildDiagnostic;
        _network.SourceFramesAvailable += ChildSourceFramesAvailable;
        _network.PreviewFramesPresented += ChildPreviewFramesPresented;
    }

    public event EventHandler<DriveInputDiagnostic>? Diagnostic;
    public event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged;
    public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
    public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented;

    public IReadOnlyList<CameraChoice> CameraChoices => _cameraChoices;
    public string SelectedCameraId => _selectedCameraId;
    public bool ReportsPreviewFrames => _selectedCameraId == DriveInputIds.NetworkLlHls;
    public bool SupportsNetworkStreams => true;
    public IReadOnlyList<DriveSourceCapability> SourceCapabilities => _sourceCapabilities;
    public bool IsReady => _selectedCameraId == DriveInputIds.NetworkLlHls
        ? _network.IsReady
        : _webcam.IsReady;

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
            if (_running)
            {
                return;
            }

            await StartSelectedAsync(cancellationToken);
            _running = true;
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
            await StopSelectedAsync();
            _running = false;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cameraId)
            || string.Equals(cameraId, _selectedCameraId, StringComparison.Ordinal))
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var restart = _running;
            await InitializeCoreAsync(cameraId, cancellationToken);
            if (restart)
            {
                await StartSelectedAsync(cancellationToken);
                _running = true;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void SetZoom(float zoomRatio) => _webcam.SetZoom(zoomRatio);

    public async Task ApplyConfigurationAsync(
        DriveInputConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var enabled = configuration.EnabledSources;
        if (enabled.Count != 1)
        {
            throw new NotSupportedException("Windows currently supports one active drive source.");
        }

        var profile = enabled[0];
        if (profile.SourceId == DriveInputIds.NetworkLlHls && profile.NetworkUrl is not null)
        {
            SetNetworkStreamUrl(profile.NetworkUrl);
        }
        await SelectCameraAsync(profile.SourceId, cancellationToken);
        SetZoom(profile.Zoom);
    }
    public void SetNetworkStreamUrl(string value)
    {
        _network.SetStreamUrl(value);
        if (_selectedCameraId == DriveInputIds.NetworkLlHls)
        {
            Diagnostic?.Invoke(this, new DriveInputDiagnostic(_network.ReadinessMessage));
        }
    }

    public void DeactivatePreview()
    {
        if (Interlocked.Exchange(ref _previewDeactivated, 1) != 0)
        {
            return;
        }

        _webcam.DeactivatePreview();
        _network.DeactivatePreview();
    }

    private async Task InitializeCoreAsync(string preferredCameraId, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        var webcamChoices = await _webcam.RefreshCameraChoicesAsync(cancellationToken);
        _cameraChoices = webcamChoices
            .Append(new CameraChoice(DriveInputIds.NetworkLlHls, "OME LL-HLS stream"))
            .ToArray();
        _sourceCapabilities =
        [
            .. webcamChoices.Select(choice => new DriveSourceCapability(
                choice.Id, choice.Name, DriveSourceKind.LogicalCamera, true, choice.Id, null,
                null, null, null, 1f, 4f,
                [new(3840, 2160), new(1920, 1080), new(1280, 720)])),
            new(DriveInputIds.NetworkLlHls, "OME LL-HLS stream", DriveSourceKind.NetworkLlHls,
                false, null, null, null, null, null, 1f, 1f, [])
        ];
        CameraChoicesChanged?.Invoke(this, _cameraChoices);

        await StopSelectedAsync();
        _running = false;
        if (preferredCameraId == DriveInputIds.NetworkLlHls || webcamChoices.Count == 0)
        {
            await _webcam.ResetAsync();
            _selectedCameraId = DriveInputIds.NetworkLlHls;
            ApplyPreviewVisibility();
            Diagnostic?.Invoke(this, new DriveInputDiagnostic(_network.ReadinessMessage));
            return;
        }

        await _webcam.InitializeAsync(preferredCameraId, cancellationToken);
        _selectedCameraId = _webcam.SelectedCameraId;
        ApplyPreviewVisibility();
    }

    private Task StartSelectedAsync(CancellationToken cancellationToken) =>
        _selectedCameraId == DriveInputIds.NetworkLlHls
            ? _network.StartAsync(cancellationToken)
            : _webcam.StartAsync();

    private Task StopSelectedAsync() =>
        _selectedCameraId == DriveInputIds.NetworkLlHls
            ? _network.StopAsync()
            : _webcam.StopAsync();

    private void ApplyPreviewVisibility()
    {
        var networkSelected = _selectedCameraId == DriveInputIds.NetworkLlHls;
        _webcamPreview.Visibility = networkSelected
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;
        _networkPreview.Visibility = networkSelected
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    private void ChildDiagnostic(object? sender, DriveInputDiagnostic diagnostic) =>
        Diagnostic?.Invoke(this, diagnostic);

    private void ChildSourceFramesAvailable(object? sender, DriveFrameCountEventArgs args) =>
        SourceFramesAvailable?.Invoke(this, args);

    private void ChildPreviewFramesPresented(object? sender, DriveFrameCountEventArgs args) =>
        PreviewFramesPresented?.Invoke(this, args);

    private void ThrowIfUnavailable() => ObjectDisposedException.ThrowIf(
        _disposed || Volatile.Read(ref _previewDeactivated) != 0,
        this);

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
            await StopSelectedAsync();
            _running = false;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        _webcam.Diagnostic -= ChildDiagnostic;
        _webcam.SourceFramesAvailable -= ChildSourceFramesAvailable;
        _network.Diagnostic -= ChildDiagnostic;
        _network.SourceFramesAvailable -= ChildSourceFramesAvailable;
        _network.PreviewFramesPresented -= ChildPreviewFramesPresented;
        await _webcam.DisposeAsync().ConfigureAwait(false);
        await _network.DisposeAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }
}
