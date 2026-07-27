namespace DeveMobileLPR.Recognition;

public sealed record AnalyzedVideoFrame(
    long SourceFrameIndex,
    TimeSpan Position,
    IReadOnlyList<AnalyzedPlateRead> Reads,
    IReadOnlyList<AnalyzedPlateConfirmation> Confirmations)
{
    public bool HasDetections => Reads.Count > 0 || Confirmations.Count > 0;
}

public sealed record AnalyzedPlateRead(
    string Text,
    float OcrConfidence,
    float DetectorConfidence);

public sealed record AnalyzedPlateConfirmation(
    string NormalizedPlate,
    string DisplayPlate,
    float Confidence,
    int ObservationCount);

public sealed record VideoAnalysisResult(
    Guid Id,
    string SourcePath,
    string DisplayName,
    DateTimeOffset AnalyzedAt,
    TimeSpan Duration,
    double SourceFrameRate,
    int SourceFrameCount,
    VideoFrameSampling Sampling,
    IReadOnlyList<AnalyzedVideoFrame> Frames);