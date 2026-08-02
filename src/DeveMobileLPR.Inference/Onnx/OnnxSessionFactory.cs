using System.Diagnostics;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DeveMobileLPR.Inference.Onnx;

internal static class OnnxSessionFactory
{
    public static SessionResult Create(
        string modelPath,
        int xnnpackThreads,
        Action<string>? diagnostic,
        bool allowNnapiFp16 = false)
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
                return new SessionResult(new InferenceSession(modelPath, options), "ONNX Runtime DirectML", diagnostics);
            }
            catch (OnnxRuntimeException exception)
            {
                Report($"DirectML unavailable; using CPU: {exception.Message}");
            }
        }

        if (OperatingSystem.IsAndroid())
        {
            return CreateFastestAndroidSession(modelPath, xnnpackThreads, allowNnapiFp16, Report, diagnostics);
        }

        Report("ONNX Runtime provider: CPU");
        using var cpuOptions = CreateBaseOptions();
        return new SessionResult(new InferenceSession(modelPath, cpuOptions), "ONNX Runtime CPU", diagnostics);
    }

    private static SessionResult CreateFastestAndroidSession(
        string modelPath,
        int xnnpackThreads,
        bool allowNnapiFp16,
        Action<string> report,
        IReadOnlyList<string> diagnostics)
    {
        var candidates = new (string Name, Func<InferenceSession> Create)[]
        {
            (allowNnapiFp16 ? "NNAPI (FP16, accelerator-only)" : "NNAPI (FP32, accelerator-only)",
                () => CreateNnapiSession(modelPath, allowNnapiFp16)),
            ($"XNNPACK ({Math.Max(1, xnnpackThreads)} threads)", () => CreateXnnpackSession(modelPath, xnnpackThreads))
        };
        InferenceSession? fastest = null;
        string? fastestName = null;
        var fastestMilliseconds = double.MaxValue;

        foreach (var candidate in candidates)
        {
            InferenceSession? session = null;
            try
            {
                session = candidate.Create();
                var elapsed = Benchmark(session);
                if (elapsed is null)
                {
                    report($"ONNX Runtime candidate {candidate.Name}: model input cannot be benchmarked; selecting it without comparison.");
                    fastest?.Dispose();
                    var selected = session;
                    session = null;
                    return new SessionResult(selected, $"ONNX Runtime {candidate.Name}", diagnostics);
                }

                report($"ONNX Runtime candidate {candidate.Name}: {elapsed.Value:0.0} ms warm benchmark");
                if (elapsed.Value < fastestMilliseconds)
                {
                    fastest?.Dispose();
                    fastest = session;
                    fastestName = candidate.Name;
                    fastestMilliseconds = elapsed.Value;
                    session = null;
                }
            }
            catch (Exception exception)
            {
                report($"ONNX Runtime candidate {candidate.Name} unavailable: {exception.GetBaseException().Message}");
            }
            finally
            {
                session?.Dispose();
            }
        }

        if (fastest is not null)
        {
            report($"ONNX Runtime provider selected: {fastestName} ({fastestMilliseconds:0.0} ms)");
            return new SessionResult(fastest, $"ONNX Runtime {fastestName}", diagnostics);
        }

        report("Android hardware providers unavailable; using ONNX Runtime CPU.");
        using var cpuOptions = CreateBaseOptions();
        return new SessionResult(new InferenceSession(modelPath, cpuOptions), "ONNX Runtime CPU", diagnostics);
    }

    private static InferenceSession CreateNnapiSession(string modelPath, bool allowFp16)
    {
        using var options = CreateBaseOptions();
        var flags = NnapiFlags.NNAPI_FLAG_CPU_DISABLED;
        if (allowFp16)
        {
            flags |= NnapiFlags.NNAPI_FLAG_USE_FP16;
        }
        options.AppendExecutionProvider_Nnapi(flags);
        return new InferenceSession(modelPath, options);
    }

    private static InferenceSession CreateXnnpackSession(string modelPath, int threads)
    {
        using var options = CreateBaseOptions();
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AppendExecutionProvider("XNNPACK", new Dictionary<string, string>
        {
            ["intra_op_num_threads"] = Math.Max(1, threads).ToString(CultureInfo.InvariantCulture)
        });
        return new InferenceSession(modelPath, options);
    }

    private static double? Benchmark(InferenceSession session)
    {
        var metadata = session.InputMetadata.Single().Value;
        if (!metadata.IsTensor || metadata.Dimensions.Any(static dimension => dimension <= 0))
        {
            return null;
        }

        var shape = metadata.Dimensions.Select(static dimension => (long)dimension).ToArray();
        var elementCount = checked(metadata.Dimensions.Aggregate(1, static (count, dimension) => count * dimension));
        return metadata.ElementDataType switch
        {
            TensorElementType.Float => Benchmark(session, new float[elementCount], shape),
            TensorElementType.UInt8 => Benchmark(session, new byte[elementCount], shape),
            TensorElementType.Int8 => Benchmark(session, new sbyte[elementCount], shape),
            TensorElementType.Int32 => Benchmark(session, new int[elementCount], shape),
            TensorElementType.Int64 => Benchmark(session, new long[elementCount], shape),
            _ => (double?)null
        };
    }

    private static double Benchmark<T>(InferenceSession session, T[] input, long[] shape)
        where T : unmanaged
    {
        using var inputValue = OrtValue.CreateTensorValueFromMemory(input, shape);
        var inputs = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
        {
            [session.InputNames.Single()] = inputValue
        };
        using var runOptions = new RunOptions();

        // The first execution finishes provider compilation and cache setup. Measuring two
        // subsequent runs avoids permanently choosing a provider based on one cold launch.
        RunOnce(session, runOptions, inputs);
        var startedAt = Stopwatch.GetTimestamp();
        RunOnce(session, runOptions, inputs);
        RunOnce(session, runOptions, inputs);
        return Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds / 2;
    }

    private static void RunOnce(
        InferenceSession session,
        RunOptions runOptions,
        IReadOnlyDictionary<string, OrtValue> inputs)
    {
        using var outputs = session.Run(runOptions, inputs, session.OutputNames);
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
