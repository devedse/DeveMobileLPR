using Microsoft.ML.OnnxRuntime;

namespace DeveMobileLPR.Inference.Onnx;

internal static class OnnxSessionFactory
{
    public static SessionResult Create(
        string modelPath,
        Action<string>? diagnostic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("ONNX model was not found.", modelPath);
        }

        var diagnostics = new List<string>();
        void Report(string message)
        {
            diagnostics.Add(message);
            diagnostic?.Invoke(message);
        }

        if (OperatingSystem.IsWindows()
            && OrtEnv.Instance().GetAvailableProviders().Contains("DmlExecutionProvider", StringComparer.Ordinal))
        {
            try
            {
                using var options = CreateBaseOptions();
                options.EnableMemoryPattern = false;
                options.AppendExecutionProvider_DML(0);
                Report("ONNX Runtime provider: DirectML");
                return new SessionResult(
                    new InferenceSession(modelPath, options),
                    "ONNX Runtime DirectML",
                    diagnostics);
            }
            catch (OnnxRuntimeException exception)
            {
                Report($"DirectML unavailable; using CPU: {exception.Message}");
            }
        }

        Report("ONNX Runtime provider: CPU");
        using var cpuOptions = CreateBaseOptions();
        return new SessionResult(
            new InferenceSession(modelPath, cpuOptions),
            "ONNX Runtime CPU",
            diagnostics);
    }

    private static SessionOptions CreateBaseOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        InterOpNumThreads = 1,
        IntraOpNumThreads = 0,
        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
    };

    internal readonly record struct SessionResult(
        InferenceSession Session,
        string BackendName,
        IReadOnlyList<string> Diagnostics);
}