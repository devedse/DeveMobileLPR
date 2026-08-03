using DeveMobileLPR.Geometry;

namespace DeveMobileLPR.Recognition;

/// <summary>
/// Contains every recognition tuning value used by live drive and video analysis.
/// Properties are grouped by their prefix so this single object can later be
/// persisted and edited without introducing parallel option types.
/// </summary>
public sealed class RecognitionTuningConfiguration
{
    // Detector and per-frame pipeline
    public float Detector_ConfidenceThreshold { get; set; } = 0.32f;
    public NormalizedRegion Detector_RoadRegion { get; set; } = new(0f, 0.18f, 1f, 0.94f);
    public float Detector_MinimumPlateWidthPixels { get; set; } = 12f;
    public float Detector_MinimumPlateHeightPixels { get; set; } = 5f;
    // These match the constants embedded in the original end-to-end ONNX model.
    public float Detector_NonMaximumSuppressionIntersectionOverUnionThreshold { get; set; } = 0.45f;
    public int Detector_MaximumDetectionsPerFrame { get; set; } = 100;
    public int Detector_MaximumOcrAttemptsPerFrame { get; set; } = 6;

    // Crop quality estimator
    public float CropQuality_MinimumCropWidthPixels { get; set; } = 48f;
    public float CropQuality_MinimumCropHeightPixels { get; set; } = 12f;
    // Projected bounds are clamped to the frame, so zero rejects only crops that actually reach an edge.
    public float CropQuality_FrameEdgeMarginPixels { get; set; } = 0f;
    public int CropQuality_SampleColumns { get; set; } = 24;
    public int CropQuality_SampleRows { get; set; } = 10;
    public float CropQuality_SharpnessNormalization { get; set; } = 55f;
    public float CropQuality_TargetLuminance { get; set; } = 130f;
    public float CropQuality_ExposureRange { get; set; } = 130f;
    public float CropQuality_FullSizeWidthPixels { get; set; } = 140f;
    public float CropQuality_SharpnessWeight { get; set; } = 0.55f;
    public float CropQuality_ExposureWeight { get; set; } = 0.25f;
    public float CropQuality_SizeWeight { get; set; } = 0.20f;
    public float CropQuality_MinimumScore { get; set; } = 0.05f;

    // Track lifetime and association gates
    public TimeSpan Tracking_TrackTimeout { get; set; } = TimeSpan.FromSeconds(1.5);
    public int Tracking_MaximumObservationsPerTrack { get; set; } = 12;
    public float Tracking_MinimumPreviousIntersectionOverUnion { get; set; } = 0.18f;
    public float Tracking_MaximumExactTextCenterDistanceFraction { get; set; } = 0.18f;
    public float Tracking_MaximumSimilarTextCenterDistanceFraction { get; set; } = 0.06f;
    public float Tracking_MaximumExactOrSimilarScaleRatio { get; set; } = 2.5f;
    public float Tracking_MaximumMotionScaleRatio { get; set; } = 2.0f;
    public float Tracking_MinimumPredictedIntersectionOverUnion { get; set; } = 0.08f;
    public float Tracking_MaximumPredictedCenterDistanceInPlateWidths { get; set; } = 1.5f;
    public float Tracking_MaximumPredictionSteps { get; set; } = 3f;
    public int Tracking_MinimumPartialTextLength { get; set; } = 3;
    public float Tracking_PredictionMinimumScale { get; set; } = 0.5f;
    public float Tracking_PredictionMaximumScale { get; set; } = 2.5f;

    // Association candidate ranking
    public float AssociationScore_DistanceWeight { get; set; } = 0.55f;
    public float AssociationScore_ScaleWeight { get; set; } = 0.25f;
    public float AssociationScore_OverlapWeight { get; set; } = 0.20f;

    // Normal temporal consensus
    public int Consensus_MinimumObservations { get; set; } = 3;
    public int Consensus_MinimumPlateLength { get; set; } = 4;
    public int Consensus_MaximumPlateLength { get; set; } = 10;
    public int Consensus_MaximumSupportingEditDistance { get; set; } = 1;
    public float Consensus_MinimumWinnerShare { get; set; } = 0.78f;
    public float Consensus_MinimumWinnerMargin { get; set; } = 0.12f;
    public float Consensus_MinimumCharacterConfidence { get; set; } = 0.60f;
    public float Consensus_MinimumQualityWeight { get; set; } = 0.10f;
    public bool Consensus_RequirePlausibleDutchFormatForDutchRegion { get; set; } = true;

    // Narrow fast path for short-lived, exceptionally strong exact reads
    public bool StrongPair_Enabled { get; set; } = true;
    public int StrongPair_RequiredDistinctFrames { get; set; } = 2;
    public float StrongPair_MinimumOcrConfidence { get; set; } = 0.95f;
    public float StrongPair_MinimumQuality { get; set; } = 0.70f;
    public float StrongPair_MinimumEvidenceWeight { get; set; } = 0.25f;
    public float StrongPair_MinimumCharacterProbability { get; set; } = 0.90f;
    public float StrongPair_MinimumCharacterMargin { get; set; } = 0.50f;
    public bool StrongPair_RequirePlausibleDutchFormat { get; set; } = true;

    public void Validate()
    {
        ValidateProbability(Detector_ConfidenceThreshold, nameof(Detector_ConfidenceThreshold));
        ValidateRegion(Detector_RoadRegion);
        ValidatePositive(Detector_MinimumPlateWidthPixels, nameof(Detector_MinimumPlateWidthPixels));
        ValidatePositive(Detector_MinimumPlateHeightPixels, nameof(Detector_MinimumPlateHeightPixels));
        ValidateProbability(
            Detector_NonMaximumSuppressionIntersectionOverUnionThreshold,
            nameof(Detector_NonMaximumSuppressionIntersectionOverUnionThreshold));
        ValidatePositive(Detector_MaximumDetectionsPerFrame, nameof(Detector_MaximumDetectionsPerFrame));
        ValidatePositive(Detector_MaximumOcrAttemptsPerFrame, nameof(Detector_MaximumOcrAttemptsPerFrame));

        ValidatePositive(CropQuality_MinimumCropWidthPixels, nameof(CropQuality_MinimumCropWidthPixels));
        ValidatePositive(CropQuality_MinimumCropHeightPixels, nameof(CropQuality_MinimumCropHeightPixels));
        ValidateAtLeast(CropQuality_FrameEdgeMarginPixels, 0, nameof(CropQuality_FrameEdgeMarginPixels));
        ValidateAtLeast(CropQuality_SampleColumns, 3, nameof(CropQuality_SampleColumns));
        ValidateAtLeast(CropQuality_SampleRows, 3, nameof(CropQuality_SampleRows));
        ValidatePositive(CropQuality_SharpnessNormalization, nameof(CropQuality_SharpnessNormalization));
        ValidateProbability(CropQuality_TargetLuminance / 255f, nameof(CropQuality_TargetLuminance));
        ValidatePositive(CropQuality_ExposureRange, nameof(CropQuality_ExposureRange));
        ValidatePositive(CropQuality_FullSizeWidthPixels, nameof(CropQuality_FullSizeWidthPixels));
        ValidateProbability(CropQuality_SharpnessWeight, nameof(CropQuality_SharpnessWeight));
        ValidateProbability(CropQuality_ExposureWeight, nameof(CropQuality_ExposureWeight));
        ValidateProbability(CropQuality_SizeWeight, nameof(CropQuality_SizeWeight));
        ValidateProbability(CropQuality_MinimumScore, nameof(CropQuality_MinimumScore));
        ValidateUnitSum(
            CropQuality_SharpnessWeight + CropQuality_ExposureWeight + CropQuality_SizeWeight,
            "Crop quality weights");

        if (Tracking_TrackTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Tracking_TrackTimeout), "Track timeout must be positive.");
        }
        ValidatePositive(Tracking_MaximumObservationsPerTrack, nameof(Tracking_MaximumObservationsPerTrack));
        ValidateProbability(Tracking_MinimumPreviousIntersectionOverUnion, nameof(Tracking_MinimumPreviousIntersectionOverUnion));
        ValidatePositive(Tracking_MaximumExactTextCenterDistanceFraction, nameof(Tracking_MaximumExactTextCenterDistanceFraction));
        ValidatePositive(Tracking_MaximumSimilarTextCenterDistanceFraction, nameof(Tracking_MaximumSimilarTextCenterDistanceFraction));
        ValidateAtLeast(Tracking_MaximumExactOrSimilarScaleRatio, 1f, nameof(Tracking_MaximumExactOrSimilarScaleRatio));
        ValidateAtLeast(Tracking_MaximumMotionScaleRatio, 1f, nameof(Tracking_MaximumMotionScaleRatio));
        ValidateProbability(Tracking_MinimumPredictedIntersectionOverUnion, nameof(Tracking_MinimumPredictedIntersectionOverUnion));
        ValidatePositive(Tracking_MaximumPredictedCenterDistanceInPlateWidths, nameof(Tracking_MaximumPredictedCenterDistanceInPlateWidths));
        ValidatePositive(Tracking_MaximumPredictionSteps, nameof(Tracking_MaximumPredictionSteps));
        ValidatePositive(Tracking_MinimumPartialTextLength, nameof(Tracking_MinimumPartialTextLength));
        ValidatePositive(Tracking_PredictionMinimumScale, nameof(Tracking_PredictionMinimumScale));
        ValidateAtLeast(Tracking_PredictionMaximumScale, Tracking_PredictionMinimumScale, nameof(Tracking_PredictionMaximumScale));

        ValidateProbability(AssociationScore_DistanceWeight, nameof(AssociationScore_DistanceWeight));
        ValidateProbability(AssociationScore_ScaleWeight, nameof(AssociationScore_ScaleWeight));
        ValidateProbability(AssociationScore_OverlapWeight, nameof(AssociationScore_OverlapWeight));
        ValidateUnitSum(
            AssociationScore_DistanceWeight + AssociationScore_ScaleWeight + AssociationScore_OverlapWeight,
            "Association score weights");

        ValidatePositive(Consensus_MinimumObservations, nameof(Consensus_MinimumObservations));
        ValidatePositive(Consensus_MinimumPlateLength, nameof(Consensus_MinimumPlateLength));
        ValidateAtLeast(Consensus_MaximumPlateLength, Consensus_MinimumPlateLength, nameof(Consensus_MaximumPlateLength));
        if (Consensus_MaximumSupportingEditDistance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Consensus_MaximumSupportingEditDistance));
        }
        ValidateProbability(Consensus_MinimumWinnerShare, nameof(Consensus_MinimumWinnerShare));
        ValidateProbability(Consensus_MinimumWinnerMargin, nameof(Consensus_MinimumWinnerMargin));
        ValidateProbability(Consensus_MinimumCharacterConfidence, nameof(Consensus_MinimumCharacterConfidence));
        ValidateProbability(Consensus_MinimumQualityWeight, nameof(Consensus_MinimumQualityWeight));

        ValidateAtLeast(StrongPair_RequiredDistinctFrames, 2, nameof(StrongPair_RequiredDistinctFrames));
        if (StrongPair_RequiredDistinctFrames >= Consensus_MinimumObservations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StrongPair_RequiredDistinctFrames),
                "The strong fast path must require fewer frames than normal consensus.");
        }
        ValidateProbability(StrongPair_MinimumOcrConfidence, nameof(StrongPair_MinimumOcrConfidence));
        ValidateProbability(StrongPair_MinimumQuality, nameof(StrongPair_MinimumQuality));
        ValidateProbability(StrongPair_MinimumEvidenceWeight, nameof(StrongPair_MinimumEvidenceWeight));
        ValidateProbability(StrongPair_MinimumCharacterProbability, nameof(StrongPair_MinimumCharacterProbability));
        ValidateProbability(StrongPair_MinimumCharacterMargin, nameof(StrongPair_MinimumCharacterMargin));
    }

    private static void ValidateRegion(NormalizedRegion region)
    {
        if (region.Left is < 0 or > 1
            || region.Top is < 0 or > 1
            || region.Right is < 0 or > 1
            || region.Bottom is < 0 or > 1
            || region.Left >= region.Right
            || region.Top >= region.Bottom)
        {
            throw new ArgumentOutOfRangeException(nameof(Detector_RoadRegion), "Road region must be a non-empty normalized rectangle.");
        }
    }

    private static void ValidateProbability(float value, string name)
    {
        if (!float.IsFinite(value) || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(name, "The value must be between zero and one.");
        }
    }

    private static void ValidatePositive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, "The value must be positive.");
        }
    }

    private static void ValidatePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, "The value must be positive.");
        }
    }

    private static void ValidateAtLeast(float value, float minimum, string name)
    {
        if (!float.IsFinite(value) || value < minimum)
        {
            throw new ArgumentOutOfRangeException(name, $"The value must be at least {minimum}.");
        }
    }

    private static void ValidateAtLeast(int value, int minimum, string name)
    {
        if (value < minimum)
        {
            throw new ArgumentOutOfRangeException(name, $"The value must be at least {minimum}.");
        }
    }

    private static void ValidateUnitSum(float value, string name)
    {
        if (Math.Abs(value - 1f) > 0.0001f)
        {
            throw new ArgumentException($"{name} must add up to 100%.");
        }
    }
}
