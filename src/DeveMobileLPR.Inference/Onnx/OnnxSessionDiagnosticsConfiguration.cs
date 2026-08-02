namespace DeveMobileLPR.Inference.Onnx;

/// <summary>
/// Controls the deliberately expensive startup measurements used while recognition
/// diagnostics are enabled. Production sessions omit this configuration.
/// </summary>
public sealed class OnnxSessionDiagnosticsConfiguration
{
    public OnnxSessionDiagnosticsConfiguration(
        int candidateBenchmarkSamples,
        int selectedBenchmarkSamples,
        int profileSamples,
        string profileDirectory,
        int profileTopOperationCount)
    {
        if (candidateBenchmarkSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidateBenchmarkSamples));
        }

        if (selectedBenchmarkSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedBenchmarkSamples));
        }

        if (string.IsNullOrWhiteSpace(profileDirectory))
        {
            throw new ArgumentException("A profile directory is required.", nameof(profileDirectory));
        }

        if (profileSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profileSamples));
        }

        if (profileTopOperationCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profileTopOperationCount));
        }

        CandidateBenchmarkSamples = candidateBenchmarkSamples;
        SelectedBenchmarkSamples = selectedBenchmarkSamples;
        ProfileSamples = profileSamples;
        ProfileDirectory = profileDirectory;
        ProfileTopOperationCount = profileTopOperationCount;
    }

    public int CandidateBenchmarkSamples { get; }
    public int SelectedBenchmarkSamples { get; }
    public int ProfileSamples { get; }
    public string ProfileDirectory { get; }
    public int ProfileTopOperationCount { get; }
}
