namespace DeveMobileLPR.AndroidApp.Services;

internal sealed record VideoAnalysisProgress(
    int ProcessedFrames,
    int TotalFrames,
    TimeSpan Position)
{
    public double Fraction => TotalFrames == 0 ? 0 : (double)ProcessedFrames / TotalFrames;
}
