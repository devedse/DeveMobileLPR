using Microsoft.ML.OnnxRuntime;

namespace DeveMobileLPR.Inference.Onnx;

/// <summary>
/// Describes an execution provider that is selected by a platform-specific host,
/// while keeping session creation and benchmarking in the shared ONNX pipeline.
/// </summary>
public sealed class OnnxExecutionProviderConfiguration
{
    public OnnxExecutionProviderConfiguration(string backendName, Action<SessionOptions> configure)
    {
        BackendName = string.IsNullOrWhiteSpace(backendName)
            ? throw new ArgumentException("A backend name is required.", nameof(backendName))
            : backendName;
        Configure = configure ?? throw new ArgumentNullException(nameof(configure));
    }

    public string BackendName { get; }

    public Action<SessionOptions> Configure { get; }
}
