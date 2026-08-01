using System.Buffers;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Recognition;
using Xunit;

namespace DeveMobileLPR.EndToEnd.Tests;

public sealed class ThirtySecondRecognitionReplayTests
{
    [Fact]
    public async Task AnalyzeAsync_LimitsAThirtyFpsSourceAndProcessesEveryFifteenthFrame()
    {
        using var source = new GeneratedVideoFrameSource(TimeSpan.FromMinutes(2), 30);
        using var engine = new VideoAnalysisEngine(new DeterministicPipeline());

        var result = await engine.AnalyzeAsync(
            source,
            "generated.mp4",
            "Generated 30 second fixture",
            new VideoAnalysisOptions(
                new VideoFrameSampling(15),
                TimeSpan.FromSeconds(30),
                IncludeDiagnostics: true),
            progress: null,
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(30), result.Duration);
        Assert.Equal(900, result.SourceFrameCount);
        Assert.Equal(60, result.Frames.Count);
        Assert.Equal(Enumerable.Range(0, 60).Select(static index => (long)index * 15), source.RequestedFrameIndices);
        Assert.All(result.Frames, static frame => Assert.NotNull(frame.Diagnostics));
        Assert.Contains(result.Frames, static frame => frame.Confirmations.Any(
            confirmation => confirmation.NormalizedPlate == "AB1234"));
    }

    [Fact]
    public async Task VideoAnalysisAndLiveStyleProcessing_UseTheSameTrackingAndConsensusSemantics()
    {
        var indices = new long[] { 0, 15, 30 };
        using var directProcessor = new RecognitionStreamProcessor(new DeterministicPipeline());
        RecognitionStreamResult? directResult = null;
        var directConfirmations = new List<ConfirmedPlate>();
        foreach (var index in indices)
        {
            using var frame = GeneratedVideoFrameSource.CreateFrame(index, TimeSpan.FromSeconds(index / 30d));
            directResult = await directProcessor.ProcessAsync(frame, CancellationToken.None);
            directConfirmations.AddRange(directResult.Confirmations);
        }

        using var source = new GeneratedVideoFrameSource(TimeSpan.FromSeconds(1.5), 30);
        using var engine = new VideoAnalysisEngine(new DeterministicPipeline());
        var videoResult = await engine.AnalyzeAsync(
            source,
            "generated.mp4",
            "Parity fixture",
            new VideoAnalysisOptions(new VideoFrameSampling(15), IncludeDiagnostics: true),
            progress: null,
            CancellationToken.None);

        var finalDirectResult = Assert.IsType<RecognitionStreamResult>(directResult);
        var directConfirmation = Assert.Single(directConfirmations);
        var videoConfirmation = Assert.Single(videoResult.Frames.SelectMany(static frame => frame.Confirmations));
        Assert.Equal(directConfirmation.Consensus.NormalizedPlate, videoConfirmation.NormalizedPlate);
        Assert.Equal(directConfirmation.Consensus.DisplayPlate, videoConfirmation.DisplayPlate);
        Assert.Equal(directConfirmation.Consensus.ObservationCount, videoConfirmation.ObservationCount);
        Assert.Equal(
            finalDirectResult.Diagnostics.Tracks.Single().ObservationCount,
            videoResult.Frames[^1].Diagnostics!.Tracks.Single().ObservationCount);
    }

    private sealed class GeneratedVideoFrameSource(TimeSpan duration, double frameRate) : IVideoFrameSource
    {
        public VideoFrameTimeline Timeline { get; } = VideoFrameTimeline.Create(duration, frameRate, null);
        public List<long> RequestedFrameIndices { get; } = [];

        public ValueTask<Yuv420Frame?> DecodeAsync(
            long sourceFrameIndex,
            TimeSpan position,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedFrameIndices.Add(sourceFrameIndex);
            return ValueTask.FromResult<Yuv420Frame?>(CreateFrame(sourceFrameIndex, position));
        }

        public static Yuv420Frame CreateFrame(long sequence, TimeSpan position)
        {
            const int width = 160;
            const int height = 90;
            var y = MemoryPool<byte>.Shared.Rent(width * height);
            var u = MemoryPool<byte>.Shared.Rent(width * height / 4);
            var v = MemoryPool<byte>.Shared.Rent(width * height / 4);
            return new Yuv420Frame(
                sequence,
                DateTimeOffset.UnixEpoch + position,
                width,
                height,
                0,
                y,
                width * height,
                width,
                1,
                u,
                width * height / 4,
                width / 2,
                1,
                v,
                width * height / 4,
                width / 2,
                1);
        }

        public void Dispose()
        {
        }
    }

    private sealed class DeterministicPipeline : IFrameRecognitionPipeline
    {
        private static readonly BoundingBox Bounds = new(40, 30, 120, 55);

        public ValueTask<FrameRecognition> ProcessAsync(Yuv420Frame frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            const string text = "AB1234";
            var characters = text
                .Select(static character => new CharacterHypothesis([new CharacterCandidate(character, 0.96f)]))
                .ToArray();
            var observation = new PlateObservation(
                frame.Sequence,
                frame.CapturedAt,
                new PlateDetection(Bounds, 0.94f),
                new PlateRead(text, 0.96f, characters, "Netherlands", 0.98f),
                0.9f);
            return ValueTask.FromResult(new FrameRecognition(frame.Sequence, frame.CapturedAt, [observation])
            {
                SourceWidth = frame.OrientedWidth,
                SourceHeight = frame.OrientedHeight,
                RotationDegrees = frame.RotationDegrees,
                Diagnostics = new RecognitionFrameDiagnostics(
                    25,
                    new ModelExecutionTiming(0, 2, 10, 1),
                    new ModelExecutionTiming(0, 3, 8, 1),
                    1,
                    1,
                    1)
            });
        }
    }
}
