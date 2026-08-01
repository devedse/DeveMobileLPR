using DeveMobileLPR.Imaging;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application.Tests;

public sealed class VideoAnalysisServiceTests
{
    [Fact]
    public async Task AnalyzeAsyncCreatesPipelineOnceAndUsesBackendForEverySource()
    {
        var provider = new CountingPipelineProvider();
        var backend = new FakeVideoBackend();
        using var service = new VideoAnalysisService(
            new RecognitionTuningConfiguration(), provider, backend);
        var options = new VideoAnalysisOptions(VideoFrameSampling.AllFrames);

        await service.AnalyzeAsync("one", "one", options, null, null, CancellationToken.None);
        await service.AnalyzeAsync("two", "two", options, null, null, CancellationToken.None);

        Assert.Equal(1, provider.CreateCount);
        Assert.Equal(["one", "two"], backend.OpenedPaths);
    }

    private sealed class CountingPipelineProvider : IRecognitionPipelineProvider
    {
        public int CreateCount { get; private set; }
        public Task<IFrameRecognitionPipeline> CreateAsync(Action<string>? diagnostic, CancellationToken cancellationToken)
        {
            CreateCount++;
            return Task.FromResult<IFrameRecognitionPipeline>(new EmptyPipeline());
        }
    }

    private sealed class EmptyPipeline : IFrameRecognitionPipeline
    {
        public ValueTask<FrameRecognition> ProcessAsync(Yuv420Frame frame, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FrameRecognition(frame.Sequence, frame.CapturedAt, []));
    }

    private sealed class FakeVideoBackend : IVideoFileBackend
    {
        public List<string> OpenedPaths { get; } = [];
        public Task<string> StageAsync(SelectedVideoFile file, CancellationToken cancellationToken) =>
            Task.FromResult(file.FullPath ?? file.FileName);
        public Task<IVideoFrameSource> OpenFrameSourceAsync(string sourcePath, CancellationToken cancellationToken)
        {
            OpenedPaths.Add(sourcePath);
            return Task.FromResult<IVideoFrameSource>(new EmptyVideoSource());
        }
        public Task<byte[]> GetPreviewAsync(string sourcePath, TimeSpan position, CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<byte>());
    }

    private sealed class EmptyVideoSource : IVideoFrameSource
    {
        public VideoFrameTimeline Timeline { get; } = new(TimeSpan.FromMilliseconds(1), 1, 1);
        public ValueTask<Yuv420Frame?> DecodeAsync(long sourceFrameIndex, TimeSpan position, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Yuv420Frame?>(null);
        public void Dispose() { }
    }
}
