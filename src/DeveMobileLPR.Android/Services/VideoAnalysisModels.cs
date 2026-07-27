using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.AndroidApp.Services;

internal sealed record VideoAnalysisProgress(
    int ProcessedFrames,
    int TotalFrames,
    TimeSpan Position)
{
    public double Fraction => TotalFrames == 0 ? 0 : (double)ProcessedFrames / TotalFrames;
}

internal sealed record AnalyzedVideoFrame(
    long SourceFrameIndex,
    TimeSpan Position,
    IReadOnlyList<AnalyzedPlateRead> Reads,
    IReadOnlyList<AnalyzedPlateConfirmation> Confirmations)
{
    public bool HasDetections => Reads.Count > 0 || Confirmations.Count > 0;
}

internal sealed record AnalyzedPlateRead(
    string Text,
    float OcrConfidence,
    float DetectorConfidence);

internal sealed record AnalyzedPlateConfirmation(
    string NormalizedPlate,
    string DisplayPlate,
    float Confidence,
    int ObservationCount);

internal sealed record VideoAnalysisResult(
    Guid Id,
    string SourcePath,
    string DisplayName,
    DateTimeOffset AnalyzedAt,
    TimeSpan Duration,
    double SourceFrameRate,
    int SourceFrameCount,
    VideoFrameSampling Sampling,
    IReadOnlyList<AnalyzedVideoFrame> Frames);