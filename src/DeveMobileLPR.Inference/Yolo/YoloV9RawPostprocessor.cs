using DeveMobileLPR.Geometry;
using DeveMobileLPR.Inference.Preprocessing;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Inference.Yolo;

internal static class YoloV9RawPostprocessor
{
    private const int BoxCoordinateCount = 4;

    public static IReadOnlyList<PlateDetection> Process(
        ReadOnlySpan<float> boxes,
        ReadOnlySpan<float> scores,
        LetterboxTransform transform,
        int frameWidth,
        int frameHeight,
        RecognitionTuningConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (boxes.Length != checked(scores.Length * BoxCoordinateCount))
        {
            throw new InvalidDataException(
                $"Expected four box coordinates per score, got {boxes.Length} coordinates and {scores.Length} scores.");
        }

        var candidates = new List<PlateDetection>();
        for (var index = 0; index < scores.Length; index++)
        {
            var score = scores[index];
            if (!float.IsFinite(score) || score < configuration.Detector_ConfidenceThreshold)
            {
                continue;
            }

            var offset = index * BoxCoordinateCount;
            var modelBounds = new BoundingBox(
                boxes[offset],
                boxes[offset + 1],
                boxes[offset + 2],
                boxes[offset + 3]);
            if (!AreFinite(modelBounds))
            {
                continue;
            }

            var sourceBounds = transform.ToSource(modelBounds, frameWidth, frameHeight);
            if (!sourceBounds.IsEmpty
                && sourceBounds.Width >= configuration.Detector_MinimumPlateWidthPixels
                && sourceBounds.Height >= configuration.Detector_MinimumPlateHeightPixels)
            {
                candidates.Add(new PlateDetection(sourceBounds, score));
            }
        }

        candidates.Sort(static (left, right) => right.Confidence.CompareTo(left.Confidence));
        var detections = new List<PlateDetection>(Math.Min(
            candidates.Count,
            configuration.Detector_MaximumDetectionsPerFrame));
        var suppressed = new bool[candidates.Count];
        for (var candidateIndex = 0;
             candidateIndex < candidates.Count
             && detections.Count < configuration.Detector_MaximumDetectionsPerFrame;
             candidateIndex++)
        {
            if (suppressed[candidateIndex])
            {
                continue;
            }

            var winner = candidates[candidateIndex];
            detections.Add(winner);
            for (var otherIndex = candidateIndex + 1; otherIndex < candidates.Count; otherIndex++)
            {
                if (!suppressed[otherIndex]
                    && winner.Bounds.IntersectionOverUnion(candidates[otherIndex].Bounds)
                        > configuration.Detector_NonMaximumSuppressionIntersectionOverUnionThreshold)
                {
                    suppressed[otherIndex] = true;
                }
            }
        }

        return detections;
    }

    private static bool AreFinite(BoundingBox bounds) =>
        float.IsFinite(bounds.Left)
        && float.IsFinite(bounds.Top)
        && float.IsFinite(bounds.Right)
        && float.IsFinite(bounds.Bottom);
}
