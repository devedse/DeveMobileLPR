using Microsoft.ML.OnnxRuntime;

namespace DeveMobileLPR.Inference.Onnx;

internal static class OnnxSessionFactory
{
    public static InferenceSession Create(string modelPath, int xnnpackThreads, Action<string>? diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("ONNX model was not found.", modelPath);
        }

        if (OperatingSystem.IsAndroid())
        {
            try
            {
                using var options = CreateBaseOptions();
                options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
                options.AppendExecutionProvider("XNNPACK", new Dictionary<string, string>
                {
                    ["intra_op_num_threads"] = Math.Max(1, xnnpackThreads).ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
                diagnostic?.Invoke("ONNX Runtime provider: XNNPACK");
                return new InferenceSession(modelPath, options);
            }
            catch (OnnxRuntimeException exception)
            {
                diagnostic?.Invoke($"XNNPACK unavailable; using CPU: {exception.Message}");
            }
        }

        diagnostic?.Invoke("ONNX Runtime provider: CPU");
        using var cpuOptions = CreateBaseOptions();
        return new InferenceSession(modelPath, cpuOptions);
    }

    private static SessionOptions CreateBaseOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        InterOpNumThreads = 1,
        IntraOpNumThreads = 0,
        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
    };
}
