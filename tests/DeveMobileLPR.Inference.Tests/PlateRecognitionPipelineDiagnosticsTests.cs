using System.Buffers;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Tests;

public sealed class PlateRecognitionPipelineDiagnosticsTests
{
    [Fact]
    public async Task ProcessAsync_PreservesFailedAndSkippedDetectorCandidatesForDiagnostics()
    {
        using var frame = CreateFrame();
        using var pipeline = new PlateRecognitionPipeline(
            new FakeDetector(),
            new EmptyRecognizer(),
            new RecognitionTuningConfiguration
            {
                Detector_MaximumOcrAttemptsPerFrame = 1
            });

        var result = await pipeline.ProcessAsync(frame, CancellationToken.None);

        Assert.Empty(result.Observations);
        Assert.Equal(2, result.Diagnostics.DetectionCount);
        Assert.Equal(1, result.Diagnostics.OcrAttemptCount);
        Assert.Equal(2, result.Diagnostics.Candidates.Count);
        Assert.True(result.Diagnostics.Candidates[0].OcrAttempted);
        Assert.Equal(string.Empty, result.Diagnostics.Candidates[0].ReadText);
        Assert.False(result.Diagnostics.Candidates[1].OcrAttempted);
        Assert.Null(result.Diagnostics.Candidates[1].OcrTiming);
        Assert.Equal(new ModelExecutionTiming(0, 1, 2, 1), result.Diagnostics.Detector);
        Assert.Equal(new DetectorPreparationTiming(0.1, 0.2, 0.7), result.Diagnostics.DetectorPreparation);
        Assert.Equal(new ModelExecutionTiming(0, 1, 3, 1), result.Diagnostics.Ocr);
        Assert.True(result.Diagnostics.CropQualityMilliseconds >= 0);
        Assert.True(result.Diagnostics.TotalMilliseconds >= result.Diagnostics.CropQualityMilliseconds);
        Assert.Equal("Test detector", result.Diagnostics.DetectorBackend);
        Assert.Equal("Test OCR", result.Diagnostics.OcrBackend);
        Assert.Equal(
            ["Detector candidate: unavailable", "OCR candidate: 12.0 ms"],
            result.Diagnostics.BackendDiagnostics);
    }

    [Fact]
    public async Task ProcessAsync_SkipsUnreadableAndFrameEdgeCropsBeforeOcr()
    {
        using var frame = CreateFrame();
        var recognizer = new EmptyRecognizer();
        using var pipeline = new PlateRecognitionPipeline(
            new FakeDetector(
                [
                    new PlateDetection(new BoundingBox(10, 10, 50, 20), 0.97f),
                    new PlateDetection(new BoundingBox(0, 10, 60, 30), 0.96f),
                    new PlateDetection(new BoundingBox(70, 10, 130, 30), 0.95f)
                ]),
            recognizer);

        var result = await pipeline.ProcessAsync(frame, CancellationToken.None);

        Assert.Equal(1, recognizer.CallCount);
        Assert.Equal(1, result.Diagnostics.OcrAttemptCount);
        Assert.Equal(3, result.Diagnostics.Candidates.Count);
        Assert.All(result.Diagnostics.Candidates.Take(2), candidate =>
        {
            Assert.Equal(0, candidate.Quality);
            Assert.False(candidate.OcrAttempted);
        });
        Assert.True(result.Diagnostics.Candidates[2].OcrAttempted);
    }

    private sealed class FakeDetector : IPlateDetector, IInferenceBackendInfo, IInferenceBackendDiagnostics
    {
        private readonly IReadOnlyList<PlateDetection> _detections;

        public FakeDetector(IReadOnlyList<PlateDetection>? detections = null)
        {
            _detections = detections ??
            [
                new PlateDetection(new BoundingBox(10, 10, 60, 30), 0.95f),
                new PlateDetection(new BoundingBox(70, 10, 120, 30), 0.85f)
            ];
        }

        public string BackendName => "Test detector";
        public IReadOnlyList<string> BackendDiagnostics => ["Detector candidate: unavailable"];

        public ValueTask<PlateDetectionResult> DetectAsync(Yuv420Frame frame, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlateDetectionResult(
                _detections,
                new ModelExecutionTiming(0, 1, 2, 1))
            {
                Preparation = new DetectorPreparationTiming(0.1, 0.2, 0.7)
            });
    }

    private sealed class EmptyRecognizer : IPlateRecognizer, IInferenceBackendInfo, IInferenceBackendDiagnostics
    {
        public int CallCount { get; private set; }

        public string BackendName => "Test OCR";
        public IReadOnlyList<string> BackendDiagnostics => ["OCR candidate: 12.0 ms"];

        public ValueTask<PlateRecognitionResult> RecognizeAsync(
            Yuv420Frame frame,
            BoundingBox plateBounds,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(new PlateRecognitionResult(
                new PlateRead(string.Empty, 0, [], null, null),
                new ModelExecutionTiming(0, 1, 3, 1)));
        }
    }

    private static Yuv420Frame CreateFrame()
    {
        const int width = 160;
        const int height = 90;
        var y = MemoryPool<byte>.Shared.Rent(width * height);
        var u = MemoryPool<byte>.Shared.Rent(width * height / 4);
        var v = MemoryPool<byte>.Shared.Rent(width * height / 4);
        return new Yuv420Frame(
            1,
            DateTimeOffset.UnixEpoch,
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
}
