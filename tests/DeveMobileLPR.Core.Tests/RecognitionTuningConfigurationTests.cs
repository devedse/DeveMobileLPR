using DeveMobileLPR.Geometry;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Tests;

public sealed class RecognitionTuningConfigurationTests
{
    [Fact]
    public void Defaults_AreTheDocumentedProductionProfile()
    {
        var configuration = new RecognitionTuningConfiguration();

        configuration.Validate();

        Assert.Equal(0.32f, configuration.Detector_ConfidenceThreshold);
        Assert.Equal(new NormalizedRegion(0.03f, 0.18f, 0.97f, 0.94f), configuration.Detector_RoadRegion);
        Assert.Equal(12f, configuration.Detector_MinimumPlateWidthPixels);
        Assert.Equal(5f, configuration.Detector_MinimumPlateHeightPixels);
        Assert.Equal(0.45f, configuration.Detector_NonMaximumSuppressionIntersectionOverUnionThreshold);
        Assert.Equal(100, configuration.Detector_MaximumDetectionsPerFrame);
        Assert.Equal(6, configuration.Detector_MaximumOcrAttemptsPerFrame);

        Assert.Equal(8f, configuration.CropQuality_MinimumCropWidthPixels);
        Assert.Equal(4f, configuration.CropQuality_MinimumCropHeightPixels);
        Assert.Equal(24, configuration.CropQuality_SampleColumns);
        Assert.Equal(10, configuration.CropQuality_SampleRows);
        Assert.Equal(55f, configuration.CropQuality_SharpnessNormalization);
        Assert.Equal(130f, configuration.CropQuality_TargetLuminance);
        Assert.Equal(130f, configuration.CropQuality_ExposureRange);
        Assert.Equal(140f, configuration.CropQuality_FullSizeWidthPixels);
        Assert.Equal(0.55f, configuration.CropQuality_SharpnessWeight);
        Assert.Equal(0.25f, configuration.CropQuality_ExposureWeight);
        Assert.Equal(0.20f, configuration.CropQuality_SizeWeight);
        Assert.Equal(0.05f, configuration.CropQuality_MinimumScore);

        Assert.Equal(TimeSpan.FromSeconds(1.5), configuration.Tracking_TrackTimeout);
        Assert.Equal(12, configuration.Tracking_MaximumObservationsPerTrack);
        Assert.Equal(0.18f, configuration.Tracking_MinimumPreviousIntersectionOverUnion);
        Assert.Equal(0.18f, configuration.Tracking_MaximumExactTextCenterDistanceFraction);
        Assert.Equal(0.06f, configuration.Tracking_MaximumSimilarTextCenterDistanceFraction);
        Assert.Equal(2.5f, configuration.Tracking_MaximumExactOrSimilarScaleRatio);
        Assert.Equal(2f, configuration.Tracking_MaximumMotionScaleRatio);
        Assert.Equal(0.08f, configuration.Tracking_MinimumPredictedIntersectionOverUnion);
        Assert.Equal(1.5f, configuration.Tracking_MaximumPredictedCenterDistanceInPlateWidths);
        Assert.Equal(3f, configuration.Tracking_MaximumPredictionSteps);
        Assert.Equal(3, configuration.Tracking_MinimumPartialTextLength);
        Assert.Equal(0.5f, configuration.Tracking_PredictionMinimumScale);
        Assert.Equal(2.5f, configuration.Tracking_PredictionMaximumScale);

        Assert.Equal(0.55f, configuration.AssociationScore_DistanceWeight);
        Assert.Equal(0.25f, configuration.AssociationScore_ScaleWeight);
        Assert.Equal(0.20f, configuration.AssociationScore_OverlapWeight);

        Assert.Equal(3, configuration.Consensus_MinimumObservations);
        Assert.Equal(4, configuration.Consensus_MinimumPlateLength);
        Assert.Equal(10, configuration.Consensus_MaximumPlateLength);
        Assert.Equal(1, configuration.Consensus_MaximumSupportingEditDistance);
        Assert.Equal(0.78f, configuration.Consensus_MinimumWinnerShare);
        Assert.Equal(0.12f, configuration.Consensus_MinimumWinnerMargin);
        Assert.Equal(0.60f, configuration.Consensus_MinimumCharacterConfidence);
        Assert.Equal(0.10f, configuration.Consensus_MinimumQualityWeight);
        Assert.True(configuration.Consensus_RequirePlausibleDutchFormatForDutchRegion);

        Assert.True(configuration.StrongPair_Enabled);
        Assert.Equal(2, configuration.StrongPair_RequiredDistinctFrames);
        Assert.Equal(0.95f, configuration.StrongPair_MinimumOcrConfidence);
        Assert.Equal(0.70f, configuration.StrongPair_MinimumQuality);
        Assert.Equal(0.25f, configuration.StrongPair_MinimumEvidenceWeight);
        Assert.Equal(0.90f, configuration.StrongPair_MinimumCharacterProbability);
        Assert.Equal(0.50f, configuration.StrongPair_MinimumCharacterMargin);
        Assert.True(configuration.StrongPair_RequirePlausibleDutchFormat);
        Assert.Equal(2, configuration.ConfirmationCorrection_MinimumAdditionalObservations);
        Assert.Equal(0.85f, configuration.ConfirmationCorrection_MinimumConfidence);
    }

    [Fact]
    public void Validate_RejectsWeightsThatDoNotAddUpToOne()
    {
        var configuration = new RecognitionTuningConfiguration
        {
            AssociationScore_DistanceWeight = 0.60f
        };

        Assert.Throws<ArgumentException>(configuration.Validate);
    }
}
