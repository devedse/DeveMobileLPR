using DeveMobileLPR.Imaging;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.AndroidApp.Recognition;

internal sealed record RecognitionProgress(
    long ProcessedFrames,
    TimeSpan LastProcessingTime,
    IReadOnlyList<PlateObservation> Observations);

internal sealed class RecognitionSession : IAsyncDisposable
{
    private readonly IFrameRecognitionPipeline _pipeline;
    private readonly ISightingRepository _repository;
    private readonly IVehicleLookup _vehicleLookup;
    private readonly Func<GeoPoint?> _location;
    private readonly LatestFrameSlot _frames = new();
    private readonly PlateTrackManager _tracks = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private long _processedFrames;

    public RecognitionSession(
        IFrameRecognitionPipeline pipeline,
        ISightingRepository repository,
        IVehicleLookup vehicleLookup,
        Func<GeoPoint?> location)
    {
        _pipeline = pipeline;
        _repository = repository;
        _vehicleLookup = vehicleLookup;
        _location = location;
        _worker = Task.Run(ProcessLoopAsync);
    }

    public event EventHandler<RecognitionProgress>? Progress;
    public event EventHandler<Sighting>? PlateConfirmed;
    public event EventHandler<Exception>? Failed;

    public bool Submit(Yuv420Frame frame) => _frames.TryWrite(frame);

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

                var recognition = await _pipeline.ProcessAsync(frame, _cancellation.Token).ConfigureAwait(false);
                var processed = Interlocked.Increment(ref _processedFrames);
                Progress?.Invoke(this, new RecognitionProgress(processed, recognition.ProcessingTime, recognition.Observations));
                foreach (var confirmation in _tracks.Update(recognition))
                {
                    var vehicle = await _vehicleLookup.FindAsync(confirmation.Consensus.NormalizedPlate, _cancellation.Token).ConfigureAwait(false);
                    var sighting = await _repository.AddOrMergeAsync(confirmation, _location(), vehicle, _cancellation.Token).ConfigureAwait(false);
                    PlateConfirmed?.Invoke(this, sighting);
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
        (_pipeline as IDisposable)?.Dispose();
    }
}
