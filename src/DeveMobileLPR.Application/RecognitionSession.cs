using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application;

internal sealed record RecognitionProgress(RecognitionStreamResult Result)
{
    public FrameRecognition Recognition => Result.Recognition;
    public RecognitionStreamDiagnostics Diagnostics => Result.Diagnostics;
}

internal sealed record RecognitionConfirmation(Sighting Sighting, ConfirmedPlate Confirmation);

internal sealed class RecognitionSession : IAsyncDisposable
{
    private readonly RecognitionStreamProcessor _processor;
    private readonly ISightingRepository _repository;
    private readonly IVehicleImageStore _vehicleImageStore;
    private readonly IVehicleLookup _vehicleLookup;
    private readonly Func<bool> _saveVehicleImages;
    private readonly Func<GeoPoint?> _location;
    private readonly Func<long?> _tripId;
    private readonly LatestFrameSlot _frames = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private int _resetRequested;

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
        _processor = new RecognitionStreamProcessor(pipeline, configuration);
        _repository = repository;
        _vehicleImageStore = vehicleImageStore;
        _vehicleLookup = vehicleLookup;
        _saveVehicleImages = saveVehicleImages;
        _location = location;
        _tripId = tripId;
        _worker = Task.Run(ProcessLoopAsync);
    }

    public event EventHandler<RecognitionProgress>? Progress;
    public event EventHandler<RecognitionConfirmation>? PlateConfirmed;
    public event EventHandler<Exception>? Failed;

    public bool Submit(Yuv420Frame frame) => _frames.TryWrite(frame);

    public void ResetTracking()
    {
        _frames.ResetStatistics();
        Interlocked.Exchange(ref _resetRequested, 1);
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                using var frame = await _frames.ReadAsync(_cancellation.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                if (Interlocked.Exchange(ref _resetRequested, 0) != 0)
                {
                    _processor.Reset();
                }

                var result = await _processor.ProcessAsync(frame, _cancellation.Token).ConfigureAwait(false);
                result = result with
                {
                    Diagnostics = result.Diagnostics with
                    {
                        ReplacedInputFrames = _frames.ReplacedFrameCount
                    }
                };
                Progress?.Invoke(this, new RecognitionProgress(result));
                foreach (var confirmation in result.Confirmations)
                {
                    var vehicle = await _vehicleLookup.FindAsync(
                        confirmation.Consensus.NormalizedPlate,
                        _cancellation.Token).ConfigureAwait(false);
                    var sighting = await _repository.AddOrMergeAsync(
                        confirmation,
                        _location(),
                        vehicle,
                        _tripId(),
                        _cancellation.Token).ConfigureAwait(false);
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
                            Failed?.Invoke(this, new InvalidOperationException(
                                "The vehicle image could not be saved.",
                                exception));
                        }
                    }

                    PlateConfirmed?.Invoke(this, new RecognitionConfirmation(sighting, confirmation));
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Failed?.Invoke(this, exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _frames.DisposeAsync().ConfigureAwait(false);
        _cancellation.Dispose();
        _processor.Dispose();
    }
}
