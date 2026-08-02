using System.Globalization;
using System.Text.Json;

namespace DeveMobileLPR.Inference.Onnx;

internal static class OnnxProfileSummary
{
    public static IReadOnlyList<string> ReadAndDelete(
        string profilePath,
        int profiledRunCount,
        int topOperationCount)
    {
        try
        {
            using var stream = File.OpenRead(profilePath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return ["ONNX profile could not be summarized: unexpected trace format."];
            }

            var operations = new Dictionary<string, OperationAggregate>(StringComparer.Ordinal);
            foreach (var traceEvent in document.RootElement.EnumerateArray())
            {
                if (!TryReadNodeDuration(traceEvent, out var operation, out var durationMicroseconds))
                {
                    continue;
                }

                operations.TryGetValue(operation, out var aggregate);
                operations[operation] = new OperationAggregate(
                    aggregate.DurationMicroseconds + durationMicroseconds,
                    aggregate.Occurrences + 1);
            }

            if (operations.Count == 0)
            {
                return ["ONNX profile contained no per-operation timing events."];
            }

            var divisor = Math.Max(1, profiledRunCount);
            var result = new List<string>
            {
                $"ONNX profile: top operations averaged across {divisor} runs"
            };
            foreach (var (operation, aggregate) in operations
                         .OrderByDescending(static item => item.Value.DurationMicroseconds)
                         .Take(Math.Max(1, topOperationCount)))
            {
                var millisecondsPerRun = aggregate.DurationMicroseconds / 1000d / divisor;
                var callsPerRun = aggregate.Occurrences / (double)divisor;
                result.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{operation}: {millisecondsPerRun:0.0} ms/run · {callsPerRun:0.#} calls/run"));
            }

            return result;
        }
        catch (Exception exception)
        {
            return [$"ONNX profile could not be summarized: {exception.GetBaseException().Message}"];
        }
        finally
        {
            TryDelete(profilePath);
        }
    }

    public static void Delete(string? profilePath)
    {
        if (!string.IsNullOrWhiteSpace(profilePath))
        {
            TryDelete(profilePath);
        }
    }

    private static bool TryReadNodeDuration(
        JsonElement traceEvent,
        out string operation,
        out double durationMicroseconds)
    {
        operation = string.Empty;
        durationMicroseconds = 0;
        if (!traceEvent.TryGetProperty("cat", out var category)
            || !string.Equals(category.GetString(), "Node", StringComparison.Ordinal)
            || !traceEvent.TryGetProperty("dur", out var duration)
            || !duration.TryGetDouble(out durationMicroseconds)
            || durationMicroseconds < 0)
        {
            return false;
        }

        if (traceEvent.TryGetProperty("args", out var arguments)
            && arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("op_name", out var operationName))
        {
            operation = operationName.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(operation)
            && traceEvent.TryGetProperty("name", out var eventName))
        {
            operation = eventName.GetString() ?? string.Empty;
        }

        return !string.IsNullOrWhiteSpace(operation);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct OperationAggregate(
        double DurationMicroseconds,
        int Occurrences);
}
