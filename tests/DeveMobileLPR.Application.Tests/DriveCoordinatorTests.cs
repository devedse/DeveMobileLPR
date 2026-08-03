using DeveMobileLPR.Imaging;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application.Tests;

#pragma warning disable CS0067 // Test double must expose the complete event contract.

public sealed class DriveCoordinatorTests
{
    [Fact]
    public async Task SharedCoordinatorOwnsInitializationAndDriveLifecycleThroughPorts()
    {
        var repository = new FakeRepository();
        var provider = new TestPipelineProvider();
        var input = new TestVideoInput();
        var device = new TestDeviceExperience();
        await using var coordinator = new DriveCoordinator(
            repository,
            new TestSettings(),
            new TestVehicleDataStatus(),
            new RecognitionTuningConfiguration(),
            provider,
            new TestVehicleLookup(),
            new TestLocationTracker(),
            device,
            new ImmediateDispatcher());

        coordinator.AttachCamera(input);
        await input.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.InitializeAsync();
        await coordinator.StartDriveAsync();

        Assert.True(coordinator.Snapshot.IsDriving);
        Assert.Equal(1, provider.CreateCount);
        Assert.Equal(1, repository.InitializeCount);
        Assert.Equal(1, repository.StartTripCount);
        Assert.Equal(1, input.StartCount);
        Assert.True(device.KeepScreenOn);

        await coordinator.StopDriveAsync();

        Assert.False(coordinator.Snapshot.IsDriving);
        Assert.Equal(1, repository.EndTripCount);
        Assert.Equal(1, input.StopCount);
        Assert.False(device.KeepScreenOn);
    }

    [Fact]
    public async Task StopDriveStillFinalizesTripAndReleasesDeviceResourcesWhenVideoInputStopFails()
    {
        var repository = new FakeRepository();
        var input = new TestVideoInput { StopException = new InvalidOperationException("decoder stop failed") };
        var location = new TestLocationTracker();
        var device = new TestDeviceExperience();
        await using var coordinator = await CreateCoordinatorAsync(repository, input, location, device);

        await coordinator.InitializeAsync();
        await coordinator.StartDriveAsync();
        await coordinator.StopDriveAsync();

        Assert.False(coordinator.Snapshot.IsDriving);
        Assert.False(coordinator.Snapshot.IsStopping);
        Assert.True(coordinator.Snapshot.HasError);
        Assert.Contains("decoder stop failed", coordinator.Snapshot.Status);
        Assert.Null(coordinator.ActiveTripId);
        Assert.Equal(1, repository.EndTripCount);
        Assert.Equal(1, input.StopCount);
        Assert.False(location.IsRunning);
        Assert.False(device.KeepScreenOn);
    }

    [Fact]
    public async Task StartDriveRollsBackTripAndLocationWhenVideoInputStartFails()
    {
        var repository = new FakeRepository();
        var input = new TestVideoInput { StartException = new InvalidOperationException("decoder start failed") };
        var location = new TestLocationTracker();
        var device = new TestDeviceExperience();
        await using var coordinator = await CreateCoordinatorAsync(repository, input, location, device);

        await coordinator.InitializeAsync();
        await coordinator.StartDriveAsync();

        Assert.False(coordinator.Snapshot.IsDriving);
        Assert.False(coordinator.Snapshot.IsStopping);
        Assert.True(coordinator.Snapshot.HasError);
        Assert.Contains("decoder start failed", coordinator.Snapshot.Status);
        Assert.Null(coordinator.ActiveTripId);
        Assert.Equal(1, repository.EndTripCount);
        Assert.Equal(1, input.StopCount);
        Assert.False(location.IsRunning);
        Assert.False(device.KeepScreenOn);
    }

    private static async Task<DriveCoordinator> CreateCoordinatorAsync(
        FakeRepository repository,
        TestVideoInput input,
        TestLocationTracker location,
        TestDeviceExperience device)
    {
        var coordinator = new DriveCoordinator(
            repository,
            new TestSettings(),
            new TestVehicleDataStatus(),
            new RecognitionTuningConfiguration(),
            new TestPipelineProvider(),
            new TestVehicleLookup(),
            location,
            device,
            new ImmediateDispatcher());
        coordinator.AttachCamera(input);
        await input.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        return coordinator;
    }

    private sealed class TestSettings : IDriveSettings
    {
        public bool TrackLocation { get; set; } = true;
        public bool ConfirmationHaptic { get; set; } = true;
        public float Zoom { get; set; } = 1;
        public string CameraId { get; set; } = "rear";
        public int RecognitionFramesPerSecond { get; set; } = 2;
        public bool TrackingDiagnosticsEnabled { get; set; }
        public bool RecognitionStatisticsEnabled { get; set; }
        public string NetworkStreamUrl { get; set; } = string.Empty;
    }

    private sealed class TestVideoInput : IDriveVideoInput
    {
        public TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public Exception? StartException { get; init; }
        public Exception? StopException { get; init; }
        public event EventHandler<DriveInputDiagnostic>? Diagnostic;
        public event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged;
        public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
        public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented;
        public IReadOnlyList<CameraChoice> CameraChoices { get; } = [new("rear", "Rear")];
        public string SelectedCameraId { get; private set; } = "rear";
        public bool IsReady { get; private set; }
        public bool SupportsNetworkStreams => true;
        public bool ReportsPreviewFrames => true;
        public Task InitializeAsync(string preferredCameraId, CancellationToken cancellationToken = default)
        {
            SelectedCameraId = preferredCameraId;
            IsReady = true;
            Initialized.TrySetResult();
            return Task.CompletedTask;
        }
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return StartException is null ? Task.CompletedTask : Task.FromException(StartException);
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return StopException is null ? Task.CompletedTask : Task.FromException(StopException);
        }
        public Task SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default)
        { SelectedCameraId = cameraId; return Task.CompletedTask; }
        public void SetZoom(float zoomRatio) { }
        public void SetNetworkStreamUrl(string value) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestPipelineProvider : IRecognitionPipelineProvider
    {
        public int CreateCount { get; private set; }
        public Task<IFrameRecognitionPipeline> CreateAsync(Action<string>? diagnostic, CancellationToken cancellationToken)
        { CreateCount++; return Task.FromResult<IFrameRecognitionPipeline>(new EmptyPipeline()); }
    }

    private sealed class EmptyPipeline : IFrameRecognitionPipeline
    {
        public ValueTask<FrameRecognition> ProcessAsync(Yuv420Frame frame, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FrameRecognition(frame.Sequence, frame.CapturedAt, []));
    }

    private sealed class TestLocationTracker : IDriveLocationTracker
    {
        public GeoPoint? Latest => null;
        public bool IsRunning { get; private set; }
        public Task<bool> StartAsync(CancellationToken cancellationToken) { IsRunning = true; return Task.FromResult(true); }
        public void Stop() => IsRunning = false;
        public void Dispose() { }
    }

    private sealed class TestDeviceExperience : IDeviceExperience
    {
        public bool KeepScreenOn { get; private set; }
        public void SetKeepScreenOn(bool enabled) => KeepScreenOn = enabled;
        public void NotifyPlateConfirmed() { }
    }

    private sealed class ImmediateDispatcher : IApplicationDispatcher
    {
        public void Dispatch(Action action) => action();
    }

    private sealed class TestVehicleDataStatus : IVehicleDataStatus { public bool IsAvailable => true; }
    private sealed class TestVehicleLookup : IVehicleLookup
    {
        public ValueTask<VehicleRecord?> FindAsync(string normalizedPlate, CancellationToken cancellationToken) =>
            ValueTask.FromResult<VehicleRecord?>(null);
    }

    private sealed class FakeRepository : ISightingRepository
    {
        public int InitializeCount { get; private set; }
        public int StartTripCount { get; private set; }
        public int EndTripCount { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken) { InitializeCount++; return Task.CompletedTask; }
        public Task<TripSummary> StartTripAsync(DateTimeOffset startedAt, GeoPoint? location, CancellationToken cancellationToken)
        { StartTripCount++; return Task.FromResult(Trip(1, startedAt, null)); }
        public Task<TripSummary> EndTripAsync(long tripId, DateTimeOffset endedAt, GeoPoint? location, CancellationToken cancellationToken)
        { EndTripCount++; return Task.FromResult(Trip(tripId, endedAt.AddSeconds(-1), endedAt)); }
        public Task AddTripPointAsync(long tripId, DateTimeOffset recordedAt, GeoPoint location, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<Sighting> AddOrMergeAsync(ConfirmedPlate plate, GeoPoint? location, VehicleRecord? vehicle, long? tripId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TripSummary>> GetTripsAsync(int offset, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TripSummary>>([]);
        public Task<TripSummary?> GetTripAsync(long tripId, CancellationToken cancellationToken) => Task.FromResult<TripSummary?>(null);
        public Task<IReadOnlyList<Sighting>> GetSightingsForTripAsync(long tripId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Sighting>>([]);
        public Task<IReadOnlyList<TripVehicleSummary>> GetVehiclesForTripAsync(long tripId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TripVehicleSummary>>([]);
        public Task<IReadOnlyList<TripPoint>> GetTripPointsAsync(long tripId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TripPoint>>([]);
        public Task<IReadOnlyList<VehicleHistorySummary>> GetVehicleHistoryAsync(VehicleHistoryQuery query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VehicleHistorySummary>>([]);
        public Task<HistoryStatistics> GetStatisticsAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken) => Task.FromResult(new HistoryStatistics(0, 0, 0, 0, null));
        public Task<IReadOnlyList<Sighting>> GetRecentAsync(int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Sighting>>([]);
        public Task<IReadOnlyList<Sighting>> GetAllSightingsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Sighting>>([]);
        public Task<IReadOnlyList<Sighting>> FindByPlateAsync(string normalizedPlate, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Sighting>>([]);
        public Task<Sighting?> GetMostExpensiveAsync(CancellationToken cancellationToken) => Task.FromResult<Sighting?>(null);
        public Task DeleteHistoryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        private static TripSummary Trip(long id, DateTimeOffset start, DateTimeOffset? end) =>
            new(id, start, end, 0, 0, 0, null, null, null, null);
    }
}

#pragma warning restore CS0067
