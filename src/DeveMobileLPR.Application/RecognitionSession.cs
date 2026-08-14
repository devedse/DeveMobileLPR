using System.Collections.Concurrent;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application;

internal sealed record RecognitionProgress(string SourceId, RecognitionStreamResult Result)
{
    public FrameRecognition Recognition => Result.Recognition;
    public RecognitionStreamDiagnostics Diagnostics => Result.Diagnostics;
}

internal sealed record RecognitionConfirmation(
    string SourceId,
    Sighting Sighting,
    ConfirmedPlate Confirmation,
    PriorVehicleSightings Prior);

internal sealed class RecognitionSession : IAsyncDisposable
{
    private readonly IFrameRecognitionPipeline _sharedPipeline;
    private readonly RecognitionTuningConfiguration _configuration;
    private readonly ISightingRepository _repository;
    private readonly IVehicleImageStore _vehicleImageStore;
    private readonly IVehicleLookup _vehicleLookup;
    private readonly Func<bool> _saveVehicleImages;
    private readonly Func<GeoPoint?> _location;
    private readonly Func<long?> _tripId;
    private readonly ConcurrentDictionary<string, Lazy<SourceWorker>> _sources = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private readonly CancellationTokenSource _cancellation = new();

    public RecognitionSession(
        IFrameRecognitionPipeline pipeline,
        RecognitionTuningConfiguration configuration,
        ISightingRepository repository,
        IVehicleImageStore vehicleImageStore,
        IVehicleLookup vehicleLookup,
        Func<bool> saveVehicleImages,
        Func<GeoPoint?> location,
        Func<long?> tripId)
    {
        _sharedPipeline = pipeline;
        _configuration = configuration;
        _repository = repository;
        _vehicleImageStore = vehicleImageStore;
        _vehicleLookup = vehicleLookup;
        _saveVehicleImages = saveVehicleImages;
        _location = location;
        _tripId = tripId;
    }

    public event EventHandler<RecognitionProgress>? Progress;
    public event EventHandler<RecognitionConfirmation>? PlateConfirmed;
    public event EventHandler<Exception>? Failed;

    public bool HasPendingFrame => _sources.Values.Any(source => source.Value.Frames.HasPendingFrame);

    public bool HasPendingFrameFor(string sourceId) =>
        _sources.TryGetValue(sourceId, out var source) && source.Value.Frames.HasPendingFrame;

    public bool Submit(string sourceId, Yuv420Frame frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        var worker = _sources.GetOrAdd(
            sourceId,
            id => new Lazy<SourceWorker>(
                () => new SourceWorker(this, id),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return worker.Value.Submit(frame);
    }

    public bool Submit(Yuv420Frame frame) => Submit("default", frame);

    public void ResetTracking()
    {
        foreach (var source in _sources.Values.Select(static source => source.Value))
        {
            source.Frames.ResetStatistics();
            Interlocked.Exchange(ref source.ResetRequested, 1);
        }
    }

    /// <summary>
    /// Waits until every source has consumed its latest frame and completed recognition and
    /// persistence. The caller must stop accepting camera frames before invoking this method.
    /// </summary>
    public async Task DrainAsync()
    {
        while (true)
        {
            var sources = _sources.Values.Select(static source => source.Value).ToArray();
            await Task.WhenAll(sources.Select(static source => source.WaitForIdleAsync()))
                .ConfigureAwait(false);
            if (sources.Length == _sources.Count && sources.All(static source => source.IsIdle))
            {
                return;
            }
        }
    }

    private async Task ProcessLoopAsync(SourceWorker source)
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                using var frame = await source.Frames.ReadAsync(_cancellation.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                try
                {
                    if (Interlocked.Exchange(ref source.ResetRequested, 0) != 0)
                    {
                        source.Processor.Reset();
                        source.SightingIdsByTrack.Clear();
                    }

                    var result = await source.Processor.ProcessAsync(frame, _cancellation.Token).ConfigureAwait(false);
                    result = result with
                    {
                        Diagnostics = result.Diagnostics with
                        {
                            ReplacedInputFrames = source.Frames.ReplacedFrameCount
                        }
                    };
                    Progress?.Invoke(this, new RecognitionProgress(source.SourceId, result));
                    foreach (var confirmation in result.Confirmations)
                    {
                        await PersistConfirmationAsync(source, frame, confirmation).ConfigureAwait(false);
                    }
                }
                finally
                {
                    source.CompleteFrame();
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                source.Processor.Reset();
                source.SightingIdsByTrack.Clear();
                ReportFailure(exception);
            }
        }
    }

    private async Task PersistConfirmationAsync(
        SourceWorker source,
        Yuv420Frame frame,
        ConfirmedPlate confirmation)
    {
        await _persistenceGate.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        try
        {
            var lookup = _vehicleLookup.FindAsync(
                confirmation.Consensus.NormalizedPlate,
                _cancellation.Token).AsTask();
            var prior = _repository.GetPriorVehicleSightingsAsync(
                confirmation.Consensus.NormalizedPlate,
                _tripId(),
                _cancellation.Token);
            await Task.WhenAll(lookup, prior).ConfigureAwait(false);
            var vehicle = await lookup.ConfigureAwait(false);
            var priorSightings = await prior.ConfigureAwait(false);
            Sighting sighting;
            if (confirmation.Revision > 0
                && source.SightingIdsByTrack.TryGetValue(confirmation.TrackId, out var sightingId))
            {
                sighting = await _repository.ReviseAsync(
                    sightingId,
                    confirmation,
                    vehicle,
                    _cancellation.Token).ConfigureAwait(false);
            }
            else
            {
                sighting = await _repository.AddOrMergeAsync(
                    confirmation,
                    _location(),
                    vehicle,
                    _tripId(),
                    _cancellation.Token).ConfigureAwait(false);
            }

            source.SightingIdsByTrack[confirmation.TrackId] = sighting.Id;
            if (_saveVehicleImages())
            {
                try
                {
                    var reference = await _vehicleImageStore.SaveAsync(
                        sighting.Id,
                        frame,
                        confirmation.LastBounds,
                        _cancellation.Token).ConfigureAwait(false);
                    sighting = await _repository.SetSnapshotReferenceAsync(
                        sighting.Id,
                        reference,
                        _cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ReportFailure(new InvalidOperationException(
                        "The vehicle image could not be saved.",
                        exception));
                }
            }

            PlateConfirmed?.Invoke(
                this,
                new RecognitionConfirmation(source.SourceId, sighting, confirmation, priorSightings));
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private void ReportFailure(Exception exception)
    {
        foreach (EventHandler<Exception> handler in Failed?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, exception);
            }
            catch
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        var workers = _sources.Values.Select(static source => source.Value).ToArray();
        foreach (var source in workers)
        {
            try
            {
                await source.Worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await source.Frames.DisposeAsync().ConfigureAwait(false);
            source.Processor.Dispose();
        }

        (_sharedPipeline as IDisposable)?.Dispose();
        _persistenceGate.Dispose();
        _cancellation.Dispose();
    }

    private sealed class SharedPipelineLease(IFrameRecognitionPipeline pipeline) : IFrameRecognitionPipeline
    {
        public ValueTask<FrameRecognition> ProcessAsync(
            Yuv420Frame frame,
            CancellationToken cancellationToken) =>
            pipeline.ProcessAsync(frame, cancellationToken);
    }

    private sealed class SourceWorker
    {
        private readonly object _idleGate = new();
        private TaskCompletionSource<bool> _idle = CompletedIdleSource();

        public SourceWorker(RecognitionSession owner, string sourceId)
        {
            SourceId = sourceId;
            Processor = new RecognitionStreamProcessor(
                new SharedPipelineLease(owner._sharedPipeline),
                owner._configuration);
            Worker = Task.Run(() => owner.ProcessLoopAsync(this));
        }

        public string SourceId { get; }
        public LatestFrameSlot Frames { get; } = new();
        public RecognitionStreamProcessor Processor { get; }
        public Dictionary<Guid, long> SightingIdsByTrack { get; } = [];
        public Task Worker { get; }
        public int ResetRequested;

        public bool IsIdle
        {
            get
            {
                lock (_idleGate)
                {
                    return _idle.Task.IsCompletedSuccessfully && !Frames.HasPendingFrame;
                }
            }
        }

        public bool Submit(Yuv420Frame frame)
        {
            lock (_idleGate)
            {
                if (_idle.Task.IsCompleted)
                {
                    _idle = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }

                if (Frames.TryWrite(frame))
                {
                    return true;
                }

                _idle.TrySetResult(true);
                return false;
            }
        }

        public Task WaitForIdleAsync()
        {
            lock (_idleGate)
            {
                return _idle.Task;
            }
        }

        public void CompleteFrame()
        {
            lock (_idleGate)
            {
                if (!Frames.HasPendingFrame)
                {
                    _idle.TrySetResult(true);
                }
            }
        }

        private static TaskCompletionSource<bool> CompletedIdleSource()
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            completion.SetResult(true);
            return completion;
        }
    }
}
