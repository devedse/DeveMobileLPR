using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Application;

/// <summary>
/// Owns UI-created video inputs. Replacement initialization waits for preceding native teardown,
/// preventing CameraX, Camera2, or MediaCapture sessions from racing for the same device.
/// </summary>
public sealed class DriveVideoInputLifetime(
    DriveCoordinator coordinator,
    IApplicationLog applicationLog)
{
    private readonly object _gate = new();
    private Task _teardownTail = Task.CompletedTask;
    private DriveVideoInputLease? _current;

    public DriveVideoInputLease Attach(IDriveVideoInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate)
        {
            if (_current is not null)
            {
                ReleaseCore(_current);
            }

            var lease = new DriveVideoInputLease(input, _teardownTail);
            _current = lease;
            coordinator.AttachCamera(lease);
            return lease;
        }
    }

    public void Release(DriveVideoInputLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            ReleaseCore(lease);
        }
    }

    private void ReleaseCore(DriveVideoInputLease lease)
    {
        if (!lease.TryRelease())
        {
            return;
        }

        coordinator.DetachCamera(lease);
        if (ReferenceEquals(_current, lease))
        {
            _current = null;
        }

        _teardownTail = DisposeAfterAsync(_teardownTail, lease);
    }

    private async Task DisposeAfterAsync(Task precedingTeardown, DriveVideoInputLease lease)
    {
        try
        {
            await precedingTeardown.ConfigureAwait(false);
            await lease.DisposeOwnedInputAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            applicationLog.Write("Camera", $"Video input cleanup failed: {exception}", true);
        }
    }
}

/// <summary>A platform-neutral lease around one UI-created native video input.</summary>
public sealed class DriveVideoInputLease : IDriveVideoInput
{
    private readonly IDriveVideoInput _inner;
    private readonly Task _precedingTeardown;
    private readonly object _gate = new();
    private Task? _initialization;
    private int _released;
    private int _disposed;

    internal DriveVideoInputLease(IDriveVideoInput inner, Task precedingTeardown)
    {
        _inner = inner;
        _precedingTeardown = precedingTeardown;
    }

    public event EventHandler<DriveInputDiagnostic>? Diagnostic
    {
        add => _inner.Diagnostic += value;
        remove => _inner.Diagnostic -= value;
    }

    public event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged
    {
        add => _inner.CameraChoicesChanged += value;
        remove => _inner.CameraChoicesChanged -= value;
    }

    public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable
    {
        add => _inner.SourceFramesAvailable += value;
        remove => _inner.SourceFramesAvailable -= value;
    }

    public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented
    {
        add => _inner.PreviewFramesPresented += value;
        remove => _inner.PreviewFramesPresented -= value;
    }

    public IReadOnlyList<CameraChoice> CameraChoices => _inner.CameraChoices;
    public string SelectedCameraId => _inner.SelectedCameraId;
    public bool IsReady => _inner.IsReady;
    public bool SupportsNetworkStreams => _inner.SupportsNetworkStreams;
    public IReadOnlyList<DriveSourceCapability> SourceCapabilities => _inner.SourceCapabilities;
    public bool ReportsPreviewFrames => _inner.ReportsPreviewFrames;

    public Task InitializeAsync(string preferredCameraId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _initialization ??= InitializeCoreAsync(preferredCameraId, cancellationToken);
            return _initialization;
        }
    }

    private async Task InitializeCoreAsync(string preferredCameraId, CancellationToken cancellationToken)
    {
        await _precedingTeardown.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _inner.InitializeAsync(preferredCameraId, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _precedingTeardown.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _inner.StopAsync(cancellationToken);

    public Task SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default) =>
        _inner.SelectCameraAsync(cameraId, cancellationToken);

    public void SetZoom(float zoomRatio) => _inner.SetZoom(zoomRatio);
    public void SetNetworkStreamUrl(string value) => _inner.SetNetworkStreamUrl(value);

    public Task ApplyConfigurationAsync(
        DriveInputConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        _inner.ApplyConfigurationAsync(configuration, cancellationToken);

    internal bool TryRelease() => Interlocked.Exchange(ref _released, 1) == 0;

    internal async ValueTask DisposeOwnedInputAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task? initialization;
        lock (_gate)
        {
            initialization = _initialization;
        }
        if (initialization is not null)
        {
            try
            {
                await initialization.ConfigureAwait(false);
            }
            catch
            {
                // Initialization already surfaced its failure through DriveCoordinator. Native
                // resources created before that failure still have to be disposed below.
            }
        }
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => DisposeOwnedInputAsync();
}
