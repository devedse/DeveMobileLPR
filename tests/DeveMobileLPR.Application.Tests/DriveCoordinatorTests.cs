using System.Buffers;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application.Tests;

#pragma warning disable CS0067 // Test double must expose the complete event contract.

public sealed class DriveCoordinatorTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task ConfirmationSavesSnapshotOnlyWhenSettingIsEnabled(bool enabled, int expectedSaveCount)
    {
        var repository = new FakeRepository();
        var pipeline = new ConfirmingPipeline();
        var vehicleImageStore = new TestVehicleImageStore();
        var input = new TestVideoInput();
        await using var coordinator = new DriveCoordinator(
            repository,
            vehicleImageStore,
            new TestSettings { SaveVehicleImages = enabled },
            new TestVehicleDataStatus(),
            new RecognitionTuningConfiguration(),
            new TestPipelineProvider(pipeline),
            new TestVehicleLookup(),
            new TestLocationTracker(),
            new TestDeviceExperience(),
            new ImmediateDispatcher());
        coordinator.AttachCamera(input);
        await input.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.InitializeAsync();
        await coordinator.StartDriveAsync();

        for (var sequence = 1; sequence <= 3; sequence++)
        {
            Assert.True(coordinator.SubmitFrame(CreateFrame(sequence)));
            await pipeline.FrameProcessed.WaitAsync(TimeSpan.FromSeconds(2));
        }

        await repository.SightingAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (enabled)
        {
            await repository.SnapshotReferenceSet.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(expectedSaveCount, vehicleImageStore.SaveCount);
        Assert.Equal(expectedSaveCount, repository.SetSnapshotReferenceCount);
    }

    [Fact]
    public async Task StrongerCorrectionReplacesVehicleImageForSameSighting()
    {
        var repository = new FakeRepository();
        var pipeline = new CorrectingPipeline();
        var vehicleImageStore = new TestVehicleImageStore();
        var input = new TestVideoInput();
        var device = new TestDeviceExperience();
        await using var coordinator = new DriveCoordinator(
            repository,
            vehicleImageStore,
            new TestSettings { SaveVehicleImages = true },
            new TestVehicleDataStatus(),
            new RecognitionTuningConfiguration { StrongPair_Enabled = false },
            new TestPipelineProvider(pipeline),
            new TestVehicleLookup(),
            new TestLocationTracker(),
            device,
            new ImmediateDispatcher());
        coordinator.AttachCamera(input);
        await input.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.InitializeAsync();
        await coordinator.StartDriveAsync();

        for (var sequence = 1; sequence <= 13; sequence++)
        {
            Assert.True(coordinator.SubmitFrame(CreateFrame(sequence)));
            await pipeline.FrameProcessed.WaitAsync(TimeSpan.FromSeconds(2));
        }

        var revised = await repository.SightingRevised.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await repository.SecondSnapshotReferenceSet.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("AA12BG", revised.NormalizedPlate);
        Assert.Equal(1, repository.ReviseCount);
        Assert.Equal(2, vehicleImageStore.SaveCount);
        Assert.Equal([1L, 1L], vehicleImageStore.SavedSightingIds);
        Assert.True(vehicleImageStore.SavedFrameSequences[1] > vehicleImageStore.SavedFrameSequences[0]);
        Assert.Equal(2, repository.SetSnapshotReferenceCount);
        Assert.Equal(1, device.NotificationCount);
    }

    [Fact]
    public async Task SharedCoordinatorOwnsInitializationAndDriveLifecycleThroughPorts()
    {
        var repository = new FakeRepository();
        var provider = new TestPipelineProvider();
        var input = new TestVideoInput();
        var device = new TestDeviceExperience();
        await using var coordinator = new DriveCoordinator(
            repository,
            new TestVehicleImageStore(),
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
    public async Task RecognitionContinuesWithLatestFrameAfterPipelineFailure()
    {
        var pipeline = new FailsOncePipeline();
        var input = new TestVideoInput();
        await using var coordinator = new DriveCoordinator(
            new FakeRepository(),
            new TestVehicleImageStore(),
            new TestSettings(),
            new TestVehicleDataStatus(),
            new RecognitionTuningConfiguration(),
            new TestPipelineProvider(pipeline),
            new TestVehicleLookup(),
            new TestLocationTracker(),
            new TestDeviceExperience(),
            new ImmediateDispatcher());
        coordinator.AttachCamera(input);
        await input.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.InitializeAsync();
        await coordinator.StartDriveAsync();

        Assert.True(coordinator.SubmitFrame(CreateFrame(1)));
        await pipeline.FirstAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(coordinator.SubmitFrame(CreateFrame(2)));

        var processedSequence = await pipeline.SuccessfulFrame.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, processedSequence);
        Assert.True(coordinator.Snapshot.IsDriving);
        Assert.True(coordinator.Snapshot.HasError);
        Assert.Contains("Scanning continues", coordinator.Snapshot.Status);
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

    [Fact]
    public async Task ConfirmedPlateOverlayPersistsAndSuppressesLiveReadings()
    {
        var repository = new FakeRepository();
        var pipeline = new ConfirmingPipeline();
        await using var coordinator = await StartDrivingAsync(repository, pipeline);
        await SubmitFramesAsync(coordinator, pipeline, 6);

        await repository.SightingAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => coordinator.Snapshot.Overlays.Any(IsConfirmed));

        var confirmed = coordinator.Snapshot.Overlays.Single(IsConfirmed);
        Assert.Equal(new BoundingBox(1, 1, 5, 3), confirmed.Bounds);
        Assert.DoesNotContain(coordinator.Snapshot.Overlays, item => item.Kind == DriveOverlayKind.Reading);
    }

    [Fact]
    public async Task ConfirmedPlateOverlayIsHighlightedWhenNew()
    {
        var repository = new FakeRepository();
        var pipeline = new ConfirmingPipeline();
        await using var coordinator = await StartDrivingAsync(repository, pipeline);
        await SubmitFramesAsync(coordinator, pipeline, 3);

        await repository.SightingAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => coordinator.Snapshot.Overlays.Any(item => item.Kind == DriveOverlayKind.ConfirmedHighlight));

        var highlighted = coordinator.Snapshot.Overlays.Single(item => item.Kind == DriveOverlayKind.ConfirmedHighlight);
        Assert.Equal("no RDW details", highlighted.Detail);
    }

    [Fact]
    public async Task ConfirmedPlateOverlayReportsSightingsFromEarlierTrips()
    {
        var repository = new FakeRepository { Prior = new PriorVehicleSightings(3, DateTimeOffset.UtcNow.AddDays(-2)) };
        var pipeline = new ConfirmingPipeline();
        await using var coordinator = await StartDrivingAsync(repository, pipeline);
        await SubmitFramesAsync(coordinator, pipeline, 4);

        await repository.SightingAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => coordinator.Snapshot.Overlays.Any(IsConfirmed));

        var confirmed = coordinator.Snapshot.Overlays.Single(IsConfirmed);
        Assert.Contains("3×", confirmed.Detail);
        Assert.Contains("2d", confirmed.Detail);
    }

    [Fact]
    public async Task ConfirmingASecondPlateKeepsTheFirstOverlayOnScreen()
    {
        var repository = new FakeRepository();
        var pipeline = new TwoPlatePipeline();
        await using var coordinator = await StartDrivingAsync(repository, pipeline);
        // Stop at the frame that confirms both plates: no later frame may rebuild the overlay list,
        // otherwise a confirmation that drops its predecessors would be papered over.
        await SubmitFramesAsync(coordinator, pipeline, 3);

        await WaitUntilAsync(() => repository.AddCount == 2);
        await WaitUntilAsync(() => coordinator.Snapshot.Overlays.Count(IsConfirmed) == 2);

        var plates = coordinator.Snapshot.Overlays.Where(IsConfirmed).Select(static item => item.Title).ToArray();
        Assert.Equal(2, plates.Distinct().Count());
    }

    [Fact]
    public async Task StopDriveRemovesConfirmedOverlays()
    {
        var repository = new FakeRepository();
        var pipeline = new ConfirmingPipeline();
        await using var coordinator = await StartDrivingAsync(repository, pipeline);
        await SubmitFramesAsync(coordinator, pipeline, 3);

        await repository.SightingAdded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => coordinator.Snapshot.Overlays.Any(IsConfirmed));

        await coordinator.StopDriveAsync();

        Assert.Empty(coordinator.Snapshot.Overlays);
    }

    private static bool IsConfirmed(DriveOverlay overlay) => overlay.Kind
        is DriveOverlayKind.Confirmed or DriveOverlayKind.ConfirmedKnown or DriveOverlayKind.ConfirmedHighlight;

    private static async Task SubmitFramesAsync(
        DriveCoordinator coordinator,
        IFrameCountingPipeline pipeline,
        int frameCount)
    {
        for (var sequence = 1; sequence <= frameCount; sequence++)
        {
            Assert.True(coordinator.SubmitFrame(CreateFrame(sequence)));
            await pipeline.FrameProcessed.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 2000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }
            await Task.Delay(10);
        }
    }

    private static async Task<DriveCoordinator> StartDrivingAsync(
        FakeRepository repository,
        IFrameRecognitionPipeline pipeline)
    {
        var input = new TestVideoInput();
        var coordinator = await CreateCoordinatorAsync(
            repository,
            input,
            new TestLocationTracker(),
            new TestDeviceExperience(),
            pipeline);
        await coordinator.InitializeAsync();
        await coordinator.StartDriveAsync();
        return coordinator;
    }

    private static async Task<DriveCoordinator> CreateCoordinatorAsync(
        FakeRepository repository,
        TestVideoInput input,
        TestLocationTracker location,
        TestDeviceExperience device,
        IFrameRecognitionPipeline? pipeline = null)
    {
        var coordinator = new DriveCoordinator(
            repository,
            new TestVehicleImageStore(),
            new TestSettings(),
            new TestVehicleDataStatus(),
            new RecognitionTuningConfiguration(),
            new TestPipelineProvider(pipeline),
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
        public bool SaveVehicleImages { get; set; }
        public bool ConfirmationHaptic { get; set; } = true;
        public float Zoom { get; set; } = 1;
        public string CameraId { get; set; } = "rear";
        public int RecognitionFramesPerSecond { get; set; } = 2;
        public bool TrackingDiagnosticsEnabled { get; set; }
        public bool RecognitionStatisticsEnabled { get; set; }
        public bool ShowRoadGuide { get; set; }
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

    private sealed class TestPipelineProvider(IFrameRecognitionPipeline? pipeline = null) : IRecognitionPipelineProvider
    {
        public int CreateCount { get; private set; }
        public Task<IFrameRecognitionPipeline> CreateAsync(Action<string>? diagnostic, CancellationToken cancellationToken)
        { CreateCount++; return Task.FromResult(pipeline ?? new EmptyPipeline()); }
    }

    private sealed class EmptyPipeline : IFrameRecognitionPipeline
    {
        public ValueTask<FrameRecognition> ProcessAsync(Yuv420Frame frame, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FrameRecognition(frame.Sequence, frame.CapturedAt, []));
    }

    private sealed class FailsOncePipeline : IFrameRecognitionPipeline
    {
        private int _attempts;
        public TaskCompletionSource FirstAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<long> SuccessfulFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<FrameRecognition> ProcessAsync(Yuv420Frame frame, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                FirstAttempted.TrySetResult();
                throw new InvalidOperationException("transient inference failure");
            }

            SuccessfulFrame.TrySetResult(frame.Sequence);
            return ValueTask.FromResult(new FrameRecognition(frame.Sequence, frame.CapturedAt, []));
        }
    }

    /// <summary>Lets a test await each processed frame instead of polling the coordinator.</summary>
    private interface IFrameCountingPipeline : IFrameRecognitionPipeline
    {
        SemaphoreSlim FrameProcessed { get; }
    }

    /// <summary>Two plates in non-overlapping boxes, so both reach consensus as separate tracks.</summary>
    private sealed class TwoPlatePipeline : IFrameCountingPipeline
    {
        public SemaphoreSlim FrameProcessed { get; } = new(0);

        public ValueTask<FrameRecognition> ProcessAsync(Yuv420Frame frame, CancellationToken cancellationToken)
        {
            PlateObservation Observe(string text, BoundingBox bounds) => new(
                frame.Sequence,
                frame.CapturedAt,
                new PlateDetection(bounds, 0.95f),
                new PlateRead(text, 0.95f, [], null, null),
                0.95f);

            FrameProcessed.Release();
            return ValueTask.FromResult(new FrameRecognition(
                frame.Sequence,
                frame.CapturedAt,
                [
                    Observe("AB1234", new BoundingBox(0, 0, 2, 2)),
                    Observe("CD5678", new BoundingBox(4, 2, 6, 4))
                ])
            {
                SourceWidth = frame.OrientedWidth,
                SourceHeight = frame.OrientedHeight,
                RotationDegrees = frame.RotationDegrees
            });
        }
    }

    private sealed class ConfirmingPipeline : IFrameCountingPipeline
    {
        public SemaphoreSlim FrameProcessed { get; } = new(0);

        public ValueTask<FrameRecognition> ProcessAsync(Yuv420Frame frame, CancellationToken cancellationToken)
        {
            var bounds = new BoundingBox(1, 1, 5, 3);
            var observation = new PlateObservation(
                frame.Sequence,
                frame.CapturedAt,
                new PlateDetection(bounds, 0.95f),
                new PlateRead("AB1234", 0.95f, [], null, null),
                0.95f);
            FrameProcessed.Release();
            return ValueTask.FromResult(new FrameRecognition(frame.Sequence, frame.CapturedAt, [observation])
            {
                SourceWidth = frame.OrientedWidth,
                SourceHeight = frame.OrientedHeight,
                RotationDegrees = frame.RotationDegrees
            });
        }
    }

    private sealed class CorrectingPipeline : IFrameCountingPipeline
    {
        public SemaphoreSlim FrameProcessed { get; } = new(0);

        public ValueTask<FrameRecognition> ProcessAsync(Yuv420Frame frame, CancellationToken cancellationToken)
        {
            var text = frame.Sequence <= 3 ? "AA12BE" : "AA12BG";
            var bounds = new BoundingBox(1, 1, 5, 3);
            var observation = new PlateObservation(
                frame.Sequence,
                frame.CapturedAt,
                new PlateDetection(bounds, 0.95f),
                new PlateRead(text, 0.95f, [], null, null),
                0.95f);
            FrameProcessed.Release();
            return ValueTask.FromResult(new FrameRecognition(frame.Sequence, frame.CapturedAt, [observation])
            {
                SourceWidth = frame.OrientedWidth,
                SourceHeight = frame.OrientedHeight,
                RotationDegrees = frame.RotationDegrees
            });
        }
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
        public int NotificationCount { get; private set; }
        public void SetKeepScreenOn(bool enabled) => KeepScreenOn = enabled;
        public void NotifyPlateConfirmed() => NotificationCount++;
    }

    private sealed class ImmediateDispatcher : IApplicationDispatcher
    {
        public void Dispatch(Action action) => action();
    }

    private sealed class TestVehicleDataStatus : IVehicleDataStatus { public bool IsAvailable => true; }
    private sealed class TestVehicleImageStore : IVehicleImageStore
    {
        public int SaveCount { get; private set; }
        public List<long> SavedSightingIds { get; } = [];
        public List<long> SavedFrameSequences { get; } = [];
        public Task<string> SaveAsync(long sightingId, Yuv420Frame frame, BoundingBox plateBounds, CancellationToken cancellationToken)
        {
            SaveCount++;
            SavedSightingIds.Add(sightingId);
            SavedFrameSequences.Add(frame.Sequence);
            return Task.FromResult($"vehicle-snapshots/{sightingId}.jpg");
        }
        public string? ResolvePath(string? reference) => null;
        public Task DeleteAllAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestVehicleLookup : IVehicleLookup
    {
        public ValueTask<VehicleRecord?> FindAsync(string normalizedPlate, CancellationToken cancellationToken) =>
            ValueTask.FromResult<VehicleRecord?>(null);
    }

    private sealed class FakeRepository : ISightingRepository
    {
        private Sighting? _currentSighting;
        private long _nextSightingId;
        public int AddCount { get; private set; }
        public TaskCompletionSource<Sighting> SightingAdded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Sighting> SightingRevised { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SnapshotReferenceSet { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondSnapshotReferenceSet { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int InitializeCount { get; private set; }
        public int StartTripCount { get; private set; }
        public int EndTripCount { get; private set; }
        public int ReviseCount { get; private set; }
        public int SetSnapshotReferenceCount { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken) { InitializeCount++; return Task.CompletedTask; }
        public Task<TripSummary> StartTripAsync(DateTimeOffset startedAt, GeoPoint? location, CancellationToken cancellationToken)
        { StartTripCount++; return Task.FromResult(Trip(1, startedAt, null)); }
        public Task<TripSummary> EndTripAsync(long tripId, DateTimeOffset endedAt, GeoPoint? location, CancellationToken cancellationToken)
        { EndTripCount++; return Task.FromResult(Trip(tripId, endedAt.AddSeconds(-1), endedAt)); }
        public Task AddTripPointAsync(long tripId, DateTimeOffset recordedAt, GeoPoint location, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<Sighting> AddOrMergeAsync(ConfirmedPlate plate, GeoPoint? location, VehicleRecord? vehicle, long? tripId, CancellationToken cancellationToken)
        {
            AddCount++;
            var sighting = new Sighting(
                Interlocked.Increment(ref _nextSightingId),
                plate.Consensus.NormalizedPlate,
                plate.Consensus.DisplayPlate,
                plate.Consensus.Region,
                plate.FirstSeenAt,
                plate.LastSeenAt,
                plate.Consensus.Confidence,
                plate.Consensus.ObservationCount,
                location,
                vehicle)
            {
                TripId = tripId
            };
            _currentSighting = sighting;
            SightingAdded.TrySetResult(sighting);
            return Task.FromResult(sighting);
        }
        public Task<Sighting> ReviseAsync(long sightingId, ConfirmedPlate plate, VehicleRecord? vehicle, CancellationToken cancellationToken)
        {
            var current = _currentSighting ?? throw new InvalidOperationException("No sighting exists to revise.");
            Assert.Equal(current.Id, sightingId);
            var revised = current with
            {
                NormalizedPlate = plate.Consensus.NormalizedPlate,
                DisplayPlate = plate.Consensus.DisplayPlate,
                Region = plate.Consensus.Region,
                LastSeenAt = plate.LastSeenAt,
                Confidence = plate.Consensus.Confidence,
                ObservationCount = plate.Consensus.ObservationCount,
                Vehicle = vehicle
            };
            _currentSighting = revised;
            ReviseCount++;
            SightingRevised.TrySetResult(revised);
            return Task.FromResult(revised);
        }
        public Task<Sighting> SetSnapshotReferenceAsync(long sightingId, string snapshotReference, CancellationToken cancellationToken)
        {
            var current = _currentSighting ?? throw new InvalidOperationException("No sighting exists to update.");
            Assert.Equal(current.Id, sightingId);
            SetSnapshotReferenceCount++;
            _currentSighting = current with { SnapshotReference = snapshotReference };
            SnapshotReferenceSet.TrySetResult();
            if (SetSnapshotReferenceCount == 2)
            {
                SecondSnapshotReferenceSet.TrySetResult();
            }
            return Task.FromResult(_currentSighting);
        }
        public Task<IReadOnlyList<TripSummary>> GetTripsAsync(int offset, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TripSummary>>([]);
        public Task<TripSummary?> GetTripAsync(long tripId, CancellationToken cancellationToken) => Task.FromResult<TripSummary?>(null);
        public Task<IReadOnlyList<Sighting>> GetSightingsForTripAsync(long tripId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Sighting>>([]);
        public Task<IReadOnlyList<TripVehicleSummary>> GetVehiclesForTripAsync(long tripId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TripVehicleSummary>>([]);
        public Task<IReadOnlyList<TripPoint>> GetTripPointsAsync(long tripId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TripPoint>>([]);
        public Task<IReadOnlyList<VehicleHistorySummary>> GetVehicleHistoryAsync(VehicleHistoryQuery query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VehicleHistorySummary>>([]);
        public PriorVehicleSightings Prior { get; set; } = PriorVehicleSightings.None;
        public Task<PriorVehicleSightings> GetPriorVehicleSightingsAsync(string normalizedPlate, long? excludeTripId, CancellationToken cancellationToken) => Task.FromResult(Prior);
        public Task<HistoryStatistics> GetStatisticsAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken) => Task.FromResult(new HistoryStatistics(0, 0, 0, 0, null));
        public Task<IReadOnlyList<Sighting>> GetRecentAsync(int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Sighting>>([]);
        public Task<IReadOnlyList<Sighting>> GetAllSightingsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Sighting>>([]);
        public Task<IReadOnlyList<Sighting>> FindByPlateAsync(string normalizedPlate, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Sighting>>([]);
        public Task<Sighting?> GetMostExpensiveAsync(CancellationToken cancellationToken) => Task.FromResult<Sighting?>(null);
        public Task DeleteHistoryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        private static TripSummary Trip(long id, DateTimeOffset start, DateTimeOffset? end) =>
            new(id, start, end, 0, 0, 0, null, null, null, null);
    }

    private static Yuv420Frame CreateFrame(long sequence)
    {
        const int width = 6;
        const int height = 4;
        const int chromaLength = 6;
        return new Yuv420Frame(
            sequence,
            DateTimeOffset.UtcNow.AddMilliseconds(sequence * 100),
            width,
            height,
            0,
            new ArrayMemoryOwner(width * height, 128),
            width * height,
            width,
            1,
            new ArrayMemoryOwner(chromaLength, 128),
            chromaLength,
            width / 2,
            1,
            new ArrayMemoryOwner(chromaLength, 128),
            chromaLength,
            width / 2,
            1);
    }

    private sealed class ArrayMemoryOwner(int length, byte value) : IMemoryOwner<byte>
    {
        private byte[]? _bytes = Enumerable.Repeat(value, length).ToArray();
        public Memory<byte> Memory => _bytes ?? throw new ObjectDisposedException(nameof(ArrayMemoryOwner));
        public void Dispose() => _bytes = null;
    }
}

#pragma warning restore CS0067
