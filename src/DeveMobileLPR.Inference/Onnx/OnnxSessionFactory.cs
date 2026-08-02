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
        bool allowNnapiFp16 = false,
        IReadOnlyList<OnnxExecutionProviderConfiguration>? preferredAndroidProviders = null,
        OnnxSessionDiagnosticsConfiguration? diagnosticsConfiguration = null)
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
            if (preferredAndroidProviders is { Count: > 0 })
            {
                var preferred = TryCreatePreferredAndroidSession(
                    modelPath,
                    preferredAndroidProviders,
                    diagnosticsConfiguration,
                    Report,
                    diagnostics);
                if (preferred is not null)
                {
                    return preferred.Value;
                }
            }

            return CreateFastestAndroidSession(modelPath, xnnpackThreads, allowNnapiFp16, Report, diagnostics);
        }

        Report("ONNX Runtime provider: CPU");
        using var cpuOptions = CreateBaseOptions();
        return new SessionResult(new InferenceSession(modelPath, cpuOptions), "ONNX Runtime CPU", diagnostics);
    }

    private static SessionResult? TryCreatePreferredAndroidSession(
        string modelPath,
        IReadOnlyList<OnnxExecutionProviderConfiguration> providers,
        OnnxSessionDiagnosticsConfiguration? diagnosticsConfiguration,
        Action<string> report,
        IReadOnlyList<string> diagnostics)
    {
        InferenceSession? fastest = null;
        string? fastestName = null;
        OnnxExecutionProviderConfiguration? fastestProvider = null;
        BenchmarkStatistics? fastestStatistics = null;
        var candidateSampleCount = diagnosticsConfiguration?.CandidateBenchmarkSamples ?? 2;

        foreach (var provider in providers)
        {
            InferenceSession? session = null;
            try
            {
                report(
                    $"ONNX Runtime benchmarking {provider.BackendName}: "
                    + $"1 warm-up + {candidateSampleCount} measured runs");
                using var options = CreateBaseOptions();
                provider.Configure(options);
                session = new InferenceSession(modelPath, options);
                var statistics = Benchmark(session, candidateSampleCount);
                if (statistics is null)
                {
                    report($"ONNX Runtime candidate {provider.BackendName}: model input cannot be benchmarked; selecting it without comparison.");
                    fastest?.Dispose();
                    var selected = session;
                    session = null;
                    return new SessionResult(selected, $"ONNX Runtime {provider.BackendName}", diagnostics);
                }

                report(
                    $"ONNX Runtime candidate {provider.BackendName}: median {statistics.Value.MedianMilliseconds:0.0} ms, "
                    + $"slowest {statistics.Value.SlowestMilliseconds:0.0} ms across {candidateSampleCount} warm runs");

                if (fastestStatistics is null
                    || statistics.Value.MedianMilliseconds < fastestStatistics.Value.MedianMilliseconds)
                {
                    if (fastest is not null)
                    {
                        fastest.Dispose();
                    }

                    fastest = session;
                    fastestName = provider.BackendName;
                    fastestProvider = provider;
                    fastestStatistics = statistics;
                    session = null;
                }
            }
            catch (Exception exception)
            {
                report($"ONNX Runtime candidate {provider.BackendName} unavailable: {exception.GetBaseException().Message}");
            }
            finally
            {
                session?.Dispose();
            }
        }

        if (fastest is null
            || fastestName is null
            || fastestProvider is null
            || fastestStatistics is null)
        {
            return null;
        }

        if (diagnosticsConfiguration is not null)
        {
            try
            {
                report(
                    $"ONNX Runtime measuring selected {fastestName}: "
                    + $"1 warm-up + {diagnosticsConfiguration.SelectedBenchmarkSamples} measured runs");
                var selectedStatistics = Benchmark(
                    fastest,
                    diagnosticsConfiguration.SelectedBenchmarkSamples);
                if (selectedStatistics is not null)
                {
                    fastestStatistics = selectedStatistics;
                    report(
                        $"ONNX Runtime selected steady benchmark: median {selectedStatistics.Value.MedianMilliseconds:0.0} ms, "
                        + $"slowest {selectedStatistics.Value.SlowestMilliseconds:0.0} ms across "
                        + $"{diagnosticsConfiguration.SelectedBenchmarkSamples} warm runs");
                }
            }
            catch (Exception exception)
            {
                report($"ONNX Runtime steady benchmark failed: {exception.GetBaseException().Message}");
            }
        }

        report(
            $"ONNX Runtime provider selected: {fastestName} "
            + $"({fastestStatistics.Value.MedianMilliseconds:0.0} ms median)");
        ProfileSelectedProvider(modelPath, fastestProvider, diagnosticsConfiguration, report);
        using var selectedOptions = CreateBaseOptions();
        fastestProvider.Configure(selectedOptions);
        InferenceSession liveSession;
        try
        {
            liveSession = new InferenceSession(modelPath, selectedOptions);
        }
        finally
        {
            fastest.Dispose();
        }

        return new SessionResult(liveSession, $"ONNX Runtime {fastestName}", diagnostics);
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
                var statistics = Benchmark(session, sampleCount: 2);
                if (statistics is null)
                {
                    report($"ONNX Runtime candidate {candidate.Name}: model input cannot be benchmarked; selecting it without comparison.");
                    fastest?.Dispose();
                    var selected = session;
                    session = null;
                    return new SessionResult(selected, $"ONNX Runtime {candidate.Name}", diagnostics);
                }

                report(
                    $"ONNX Runtime candidate {candidate.Name}: median {statistics.Value.MedianMilliseconds:0.0} ms, "
                    + $"slowest {statistics.Value.SlowestMilliseconds:0.0} ms across 2 warm runs");
                if (statistics.Value.MedianMilliseconds < fastestMilliseconds)
                {
                    fastest?.Dispose();
                    fastest = session;
                    fastestName = candidate.Name;
                    fastestMilliseconds = statistics.Value.MedianMilliseconds;
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

    private static BenchmarkStatistics? Benchmark(
        InferenceSession session,
        int sampleCount)
    {
        if (sampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        var metadata = session.InputMetadata.Single().Value;
        if (!metadata.IsTensor || metadata.Dimensions.Any(static dimension => dimension <= 0))
        {
            return null;
        }

        var shape = metadata.Dimensions.Select(static dimension => (long)dimension).ToArray();
        var elementCount = checked(metadata.Dimensions.Aggregate(1, static (count, dimension) => count * dimension));
        return metadata.ElementDataType switch
        {
            TensorElementType.Float => Benchmark(session, new float[elementCount], shape, sampleCount),
            TensorElementType.UInt8 => Benchmark(session, new byte[elementCount], shape, sampleCount),
            TensorElementType.Int8 => Benchmark(session, new sbyte[elementCount], shape, sampleCount),
            TensorElementType.Int32 => Benchmark(session, new int[elementCount], shape, sampleCount),
            TensorElementType.Int64 => Benchmark(session, new long[elementCount], shape, sampleCount),
            _ => (BenchmarkStatistics?)null
        };
    }

    private static BenchmarkStatistics Benchmark<T>(
        InferenceSession session,
        T[] input,
        long[] shape,
        int sampleCount)
        where T : unmanaged
    {
        using var inputValue = OrtValue.CreateTensorValueFromMemory(input, shape);
        var inputs = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
        {
            [session.InputNames.Single()] = inputValue
        };
        using var runOptions = new RunOptions();

        // The first execution finishes provider compilation, graph capture, and cache setup.
        // It is deliberately excluded from the warm distribution.
        RunOnce(session, runOptions, inputs);
        var samples = new double[sampleCount];
        for (var index = 0; index < samples.Length; index++)
        {
            var startedAt = Stopwatch.GetTimestamp();
            RunOnce(session, runOptions, inputs);
            samples[index] = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        }

        Array.Sort(samples);
        var middle = samples.Length / 2;
        var median = samples.Length % 2 == 0
            ? (samples[middle - 1] + samples[middle]) / 2
            : samples[middle];
        return new BenchmarkStatistics(median, samples[^1]);
    }

    private static void RunOnce(
        InferenceSession session,
        RunOptions runOptions,
        IReadOnlyDictionary<string, OrtValue> inputs)
    {
        using var outputs = session.Run(runOptions, inputs, session.OutputNames);
    }

    private static void ProfileSelectedProvider(
        string modelPath,
        OnnxExecutionProviderConfiguration provider,
        OnnxSessionDiagnosticsConfiguration? configuration,
        Action<string> report)
    {
        if (configuration is null)
        {
            return;
        }

        string? profilePath = null;
        try
        {
            PrepareProfileDirectory(configuration.ProfileDirectory);
            report(
                $"ONNX Runtime profiling {provider.BackendName}: "
                + $"1 warm-up + {configuration.ProfileSamples} instrumented runs");
            using var options = CreateBaseOptions();
            options.ProfileOutputPathPrefix = Path.Combine(
                configuration.ProfileDirectory,
                $"onnx-webgpu-{Guid.NewGuid():N}");
            options.EnableProfiling = true;
            provider.Configure(options);
            using var session = new InferenceSession(modelPath, options);
            var statistics = Benchmark(session, configuration.ProfileSamples);
            profilePath = session.EndProfiling();

            if (statistics is not null)
            {
                report(
                    $"ONNX Runtime profiling pass: median {statistics.Value.MedianMilliseconds:0.0} ms across "
                    + $"{configuration.ProfileSamples} instrumented warm runs; excluded from provider selection");
            }

            foreach (var summary in OnnxProfileSummary.ReadAndDelete(
                         profilePath,
                         configuration.ProfileSamples + 1,
                         configuration.ProfileTopOperationCount))
            {
                report(summary);
            }

            profilePath = null;
        }
        catch (Exception exception)
        {
            report($"ONNX profile unavailable: {exception.GetBaseException().Message}");
        }
        finally
        {
            OnnxProfileSummary.Delete(profilePath);
            DeleteProfileFiles(configuration.ProfileDirectory);
        }
    }

    private static void PrepareProfileDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        DeleteProfileFiles(directory);
    }

    private static void DeleteProfileFiles(string directory)
    {
        try
        {
            foreach (var staleProfile in Directory.EnumerateFiles(directory, "onnx-*.json"))
            {
                OnnxProfileSummary.Delete(staleProfile);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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

    private readonly record struct BenchmarkStatistics(
        double MedianMilliseconds,
        double SlowestMilliseconds);
}
