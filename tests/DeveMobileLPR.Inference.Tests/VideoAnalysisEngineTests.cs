using System.Buffers;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Tests;

public sealed class VideoAnalysisEngineTests
{
    [Fact]
    public async Task AnalyzeAsync_AppliesSamplingAndReportsEveryRequestedFrame()
    {
        using var source = new FakeVideoFrameSource();
        using var engine = new VideoAnalysisEngine(new FakePipeline());
        var progress = new List<VideoAnalysisProgress>();

        var result = await engine.AnalyzeAsync(
            source,
            "video.mp4",
            "Video",
            new VideoFrameSampling(2),
            new InlineProgress<VideoAnalysisProgress>(progress.Add),
            CancellationToken.None);

        Assert.Equal([0L, 2L, 4L], source.RequestedFrameIndices);
        Assert.Equal(3, progress.Count);
        Assert.Equal(1, progress[^1].Fraction);
        Assert.True(progress[^1].Elapsed > TimeSpan.Zero);
        Assert.True(progress[^1].FramesPerSecond > 0);
        Assert.Equal([0L, 2L], result.Frames.Select(static frame => frame.SourceFrameIndex));
        Assert.Equal(source.Timeline, new VideoFrameTimeline(result.Duration, result.SourceFrameRate, result.SourceFrameCount));
        Assert.Equal(new VideoFrameSampling(2), result.Sampling);
    }

    private sealed class FakeVideoFrameSource : IVideoFrameSource
    {
        public VideoFrameTimeline Timeline { get; } = VideoFrameTimeline.Create(TimeSpan.FromSeconds(6), 1, 6);
        public List<long> RequestedFrameIndices { get; } = [];

        public ValueTask<Yuv420Frame?> DecodeAsync(
            long sourceFrameIndex,
            TimeSpan position,
            CancellationToken cancellationToken)
        {
            RequestedFrameIndices.Add(sourceFrameIndex);
            return ValueTask.FromResult<Yuv420Frame?>(sourceFrameIndex == 4 ? null : CreateFrame(sourceFrameIndex, position));
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakePipeline : IFrameRecognitionPipeline
    {
        public ValueTask<FrameRecognition> ProcessAsync(Yuv420Frame frame, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FrameRecognition(frame.Sequence, frame.CapturedAt, []));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static Yuv420Frame CreateFrame(long sequence, TimeSpan position)
    {
        const int width = 2;
        const int height = 2;
        var y = MemoryPool<byte>.Shared.Rent(4);
        var u = MemoryPool<byte>.Shared.Rent(1);
        var v = MemoryPool<byte>.Shared.Rent(1);
        return new Yuv420Frame(
            sequence,
            DateTimeOffset.UnixEpoch + position,
            width,
            height,
            0,
            y,
            4,
            width,
            1,
            u,
            1,
            1,
            1,
            v,
            1,
            1,
            1);
    }
}