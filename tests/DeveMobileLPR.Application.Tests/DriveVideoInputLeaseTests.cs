namespace DeveMobileLPR.Application.Tests;

#pragma warning disable CS0067 // The test double implements the complete production event contract.

public sealed class DriveVideoInputLeaseTests
{
    [Fact]
    public async Task InitializationWaitsForThePreviousNativeInputToFinishTearingDown()
    {
        var previousTeardown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new BlockingVideoInput();
        var lease = new DriveVideoInputLease(input, previousTeardown.Task);

        var initialization = lease.InitializeAsync("rear");

        Assert.False(input.InitializeCalled.Task.IsCompleted);
        previousTeardown.SetResult();
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(input.InitializeCalled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposalWaitsForInitializationBeforeDisposingTheNativeInput()
    {
        var input = new BlockingVideoInput { BlockInitialization = true };
        var lease = new DriveVideoInputLease(input, Task.CompletedTask);
        var initialization = lease.InitializeAsync("rear");
        await input.InitializeCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = lease.DisposeAsync().AsTask();

        Assert.False(input.Disposed.Task.IsCompleted);
        input.AllowInitialization.SetResult();
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(input.Disposed.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposalIsIdempotent()
    {
        var input = new BlockingVideoInput();
        var lease = new DriveVideoInputLease(input, Task.CompletedTask);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(1, input.DisposeCount);
    }

    [Fact]
    public async Task DisposalWaitsForAStartAlreadyInProgress()
    {
        var input = new BlockingVideoInput { BlockStart = true };
        var lease = new DriveVideoInputLease(input, Task.CompletedTask, generation: 42);

        var start = lease.StartAsync();
        await input.StartCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(lease.TryRelease());

        var disposal = lease.DisposeAsync().AsTask();
        Assert.False(input.Disposed.Task.IsCompleted);

        input.AllowStart.SetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(2));
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(input.Disposed.Task.IsCompletedSuccessfully);
        Assert.Equal(42, lease.Generation);
    }

    [Fact]
    public async Task ReleasedLeaseRejectsAStartInsteadOfTouchingDisposedSynchronizationState()
    {
        var input = new BlockingVideoInput();
        var lease = new DriveVideoInputLease(input, Task.CompletedTask, generation: 7);

        Assert.True(lease.TryRelease());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => lease.StartAsync());
        await lease.DisposeAsync();

        Assert.Contains("#7", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, input.StartCount);
        Assert.Equal(1, input.DisposeCount);
    }

    private sealed class BlockingVideoInput : IDriveVideoInput
    {
        public bool BlockInitialization { get; init; }
        public bool BlockStart { get; init; }
        public TaskCompletionSource InitializeCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowInitialization { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StartCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowStart { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }
        public int StartCount { get; private set; }

        public event EventHandler<DriveInputDiagnostic>? Diagnostic;
        public event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged;
        public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
        public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented;

        public IReadOnlyList<CameraChoice> CameraChoices => [];
        public string SelectedCameraId => "rear";
        public bool IsReady => InitializeCalled.Task.IsCompletedSuccessfully;
        public bool SupportsNetworkStreams => false;
        public IReadOnlyList<DriveSourceCapability> SourceCapabilities => [];
        public bool ReportsPreviewFrames => false;

        public async Task InitializeAsync(string preferredCameraId, CancellationToken cancellationToken = default)
        {
            InitializeCalled.TrySetResult();
            if (BlockInitialization)
            {
                await AllowInitialization.Task.WaitAsync(cancellationToken);
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            StartCalled.TrySetResult();
            if (BlockStart)
            {
                await AllowStart.Task.WaitAsync(cancellationToken);
            }
        }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public void SetZoom(float zoomRatio) { }
        public void SetNetworkStreamUrl(string value) { }
        public Task ApplyConfigurationAsync(
            DriveInputConfiguration configuration,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
