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
    FrameRecognition Recognition,
    IReadOnlyList<ConfirmedPlate> Confirmations);

internal sealed record VideoAnalysisResult(
    string SourcePath,
    string DisplayName,
    TimeSpan Duration,
    double SourceFrameRate,
    int SourceFrameCount,
    VideoFrameSampling Sampling,
    IReadOnlyList<AnalyzedVideoFrame> Frames);