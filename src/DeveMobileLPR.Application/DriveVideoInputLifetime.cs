using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Application;

/// <summary>
/// Owns UI-created video inputs. Every attachment receives a generation number, and replacement
/// initialization waits for every preceding native teardown. This prevents a newly opened drive
/// page from accidentally starting the input owned by the page that is still disappearing.
/// </summary>
public sealed class DriveVideoInputLifetime(
    DriveCoordinator coordinator,
    IApplicationLog applicationLog)
{
    private readonly object _gate = new();
    private Task _teardownTail = Task.CompletedTask;
    private DriveVideoInputLease? _current;
    private long _nextGeneration;
    private int _pendingTeardowns;

    public DriveVideoInputLease Attach(IDriveVideoInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate)
        {
            if (_current is not null)
            {
                ReleaseCore(_current);
            }

            var generation = Interlocked.Increment(ref _nextGeneration);
            var lease = new DriveVideoInputLease(input, _teardownTail, generation);
            _current = lease;
            applicationLog.Write("Camera", $"Input #{generation} attached.");
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

        applicationLog.Write("Camera", $"Input #{lease.Generation} releasing.");
        coordinator.DetachCamera(lease);
        if (ReferenceEquals(_current, lease))
        {
            _current = null;
        }

        Interlocked.Increment(ref _pendingTeardowns);
        coordinator.SetCameraTransitioning(true);
        _teardownTail = DisposeAfterAsync(_teardownTail, lease);
    }

    private async Task DisposeAfterAsync(Task precedingTeardown, DriveVideoInputLease lease)
    {
        try
        {
            await precedingTeardown.ConfigureAwait(false);
            await lease.DisposeOwnedInputAsync().ConfigureAwait(false);
            applicationLog.Write("Camera", $"Input #{lease.Generation} disposed.");
        }
        catch (Exception exception)
        {
            applicationLog.Write(
                "Camera",
                $"Input #{lease.Generation} cleanup failed: {exception}",
                true);
        }
        finally
        {
            if (Interlocked.Decrement(ref _pendingTeardowns) == 0)
            {
                coordinator.SetCameraTransitioning(false);
            }
        }
    }
}

/// <summary>
/// A generation-aware, platform-neutral lease around one UI-created native video input. Async
/// operations are serialized with release/disposal. The semaphore deliberately lives for the
/// lease's managed lifetime so a late caller receives a useful stale-generation error rather than
/// an ObjectDisposedException from SemaphoreSlim.
/// </summary>
public sealed class DriveVideoInputLease : IDriveVideoInput
{
    private readonly IDriveVideoInput _inner;
    private readonly Task _precedingTeardown;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private Task? _initialization;
    private Task? _disposal;
    private int _released;
    private int _disposed;

    internal DriveVideoInputLease(
        IDriveVideoInput inner,
        Task precedingTeardown,
        long generation = 1)
    {
        _inner = inner;
        _precedingTeardown = precedingTeardown;
        Generation = generation;
    }

    public long Generation { get; }
    public bool IsActive => Volatile.Read(ref _released) == 0 && Volatile.Read(ref _disposed) == 0;

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

    public event EventHandler<DriveZoomState>? ZoomStateChanged
    {
        add => _inner.ZoomStateChanged += value;
        remove => _inner.ZoomStateChanged -= value;
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
    public bool IsReady => IsActive && _inner.IsReady;
    public bool SupportsNetworkStreams => _inner.SupportsNetworkStreams;
    public IReadOnlyList<DriveSourceCapability> SourceCapabilities => _inner.SourceCapabilities;
    public DriveZoomState ZoomState => _inner.ZoomState;
    public bool ReportsPreviewFrames => _inner.ReportsPreviewFrames;

    public Task InitializeAsync(string preferredCameraId, CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            ThrowIfUnavailable();
            _initialization ??= RunExclusiveAsync(
                () => _inner.InitializeAsync(preferredCameraId, cancellationToken),
                cancellationToken,
                waitForPrecedingTeardown: true);
            return _initialization;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => _inner.StartAsync(cancellationToken),
            cancellationToken,
            waitForPrecedingTeardown: true);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => _inner.StopAsync(cancellationToken),
            cancellationToken,
            waitForPrecedingTeardown: false);

    public Task SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => _inner.SelectCameraAsync(cameraId, cancellationToken),
            cancellationToken,
            waitForPrecedingTeardown: true);

    public void SetZoom(float zoomRatio)
    {
        lock (_stateGate)
        {
            ThrowIfUnavailable();
            _inner.SetZoom(zoomRatio);
        }
    }

    public void SetNetworkStreamUrl(string value)
    {
        lock (_stateGate)
        {
            ThrowIfUnavailable();
            _inner.SetNetworkStreamUrl(value);
        }
    }

    public Task ApplyConfigurationAsync(
        DriveInputConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => _inner.ApplyConfigurationAsync(configuration, cancellationToken),
            cancellationToken,
            waitForPrecedingTeardown: true);

    internal bool TryRelease() => Interlocked.Exchange(ref _released, 1) == 0;

    internal ValueTask DisposeOwnedInputAsync()
    {
        lock (_stateGate)
        {
            _released = 1;
            _disposal ??= DisposeCoreAsync();
            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task RunExclusiveAsync(
        Func<Task> operation,
        CancellationToken cancellationToken,
        bool waitForPrecedingTeardown)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (waitForPrecedingTeardown)
            {
                await _precedingTeardown.WaitAsync(cancellationToken).ConfigureAwait(false);
                ThrowIfUnavailable();
            }
            await operation().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void ThrowIfUnavailable()
    {
        if (!IsActive)
        {
            throw new InvalidOperationException(
                $"Camera input #{Generation} is no longer active; wait for the newly attached input.");
        }
    }

    public ValueTask DisposeAsync() => DisposeOwnedInputAsync();
}
