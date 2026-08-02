using DeveMobileLPR.Geometry;
using DeveMobileLPR.Inference.Preprocessing;
using DeveMobileLPR.Inference.Yolo;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Tests;

public sealed class YoloV9RawPostprocessorTests
{
    [Fact]
    public void Process_RemovesOverlappingLowerConfidenceBoxes()
    {
        float[] boxes =
        [
            10, 10, 110, 50,
            12, 11, 112, 51,
            300, 200, 390, 230
        ];
        float[] scores = [0.92f, 0.87f, 0.71f];

        var detections = YoloV9RawPostprocessor.Process(
            boxes,
            scores,
            IdentityTransform,
            608,
            608,
            new RecognitionTuningConfiguration());

        Assert.Collection(
            detections,
            detection =>
            {
                Assert.Equal(new BoundingBox(10, 10, 110, 50), detection.Bounds);
                Assert.Equal(0.92f, detection.Confidence);
            },
            detection =>
            {
                Assert.Equal(new BoundingBox(300, 200, 390, 230), detection.Bounds);
                Assert.Equal(0.71f, detection.Confidence);
            });
    }

    [Fact]
    public void Process_FiltersInvalidLowConfidenceAndTooSmallCandidates()
    {
        float[] boxes =
        [
            10, 10, 110, 50,
            200, 100, 205, 102,
            float.NaN, 0, 100, 100
        ];
        float[] scores = [0.31f, 0.99f, 0.99f];

        var detections = YoloV9RawPostprocessor.Process(
            boxes,
            scores,
            IdentityTransform,
            608,
            608,
            new RecognitionTuningConfiguration());

        Assert.Empty(detections);
    }

    [Fact]
    public void Process_RejectsMismatchedOutputLengths()
    {
        var exception = Assert.Throws<InvalidDataException>(() => YoloV9RawPostprocessor.Process(
            [0, 0, 10],
            [0.9f],
            IdentityTransform,
            608,
            608,
            new RecognitionTuningConfiguration()));

        Assert.Contains("four box coordinates", exception.Message, StringComparison.Ordinal);
    }

    private static LetterboxTransform IdentityTransform { get; } =
        new(new BoundingBox(0, 0, 608, 608), 1, 0, 0);
}
