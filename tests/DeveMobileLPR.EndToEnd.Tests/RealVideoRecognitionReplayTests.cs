using System.Diagnostics;
using System.Text.Json;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Inference.Models;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Video.Windows;
using Xunit;
using Xunit.Abstractions;

namespace DeveMobileLPR.EndToEnd.Tests;

public sealed class RealVideoRecognitionReplayTests(ITestOutputHelper output)
{
    private const string VideoEnvironmentVariable = "DEVEMOBILELPR_E2E_VIDEO";
    private const string DurationEnvironmentVariable = "DEVEMOBILELPR_E2E_DURATION_SECONDS";
    private const string SamplingEnvironmentVariable = "DEVEMOBILELPR_E2E_SAMPLE_INTERVAL";
    private const string ReportEnvironmentVariable = "DEVEMOBILELPR_E2E_REPORT";

    [LocalVideoFact]
    [Trait("Category", "LocalVideoFixture")]
    public async Task AnalyzeAsync_FirstThirtySeconds_UsesProductionDecoderModelsAndStreamProcessor()
    {
        var sourcePath = Environment.GetEnvironmentVariable(VideoEnvironmentVariable)!;

        var duration = TimeSpan.FromSeconds(ReadPositiveInteger(DurationEnvironmentVariable, 30));
        var timeline = await WindowsVideoMetadataReader.ReadTimelineAsync(sourcePath, CancellationToken.None);
        var samplingInterval = ReadPositiveInteger(
            SamplingEnvironmentVariable,
            Math.Max(1, (int)Math.Round(timeline.FrameRate / 2d, MidpointRounding.AwayFromZero)));
        var options = new VideoAnalysisOptions(
            new VideoFrameSampling(samplingInterval),
            duration,
            IncludeDiagnostics: true);

        var modelDirectory = Path.Combine(AppContext.BaseDirectory, "models");
        var detectorPath = Path.Combine(modelDirectory, ModelCatalog.Detector.FileName);
        var recognizerPath = Path.Combine(modelDirectory, ModelCatalog.Recognizer.FileName);
        await ModelArtifactVerifier.VerifyAsync(detectorPath, ModelCatalog.Detector, CancellationToken.None);
        await ModelArtifactVerifier.VerifyAsync(recognizerPath, ModelCatalog.Recognizer, CancellationToken.None);

        var providerDiagnostics = new List<string>();
        var progress = new LatestProgress<VideoAnalysisProgress>();
        using var source = WindowsMediaFoundationVideoFrameSource.Create(sourcePath, timeline);
        using var engine = new VideoAnalysisEngine(
            OnnxPlateRecognitionPipelineFactory.Create(detectorPath, recognizerPath, providerDiagnostics.Add));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var startedAt = Stopwatch.GetTimestamp();
        var result = await engine.AnalyzeAsync(
            source,
            sourcePath,
            Path.GetFileName(sourcePath),
            options,
            progress,
            timeout.Token);
        var wallMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        var summary = ReplaySummary.Create(result, progress.Value, wallMilliseconds, providerDiagnostics);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        output.WriteLine(JsonSerializer.Serialize(summary, jsonOptions));
        WriteFrameDetails(result);
        await WriteOptionalReportAsync(result, summary, jsonOptions, timeout.Token);

        var expectedSourceFrameCount = Math.Min(
            timeline.FrameCount,
            Math.Max(1, checked((int)Math.Ceiling(options.EffectiveDuration(timeline).TotalSeconds * timeline.FrameRate))));
        var expectedAnalyzedFrameCount =
            (expectedSourceFrameCount + samplingInterval - 1) / samplingInterval;
        Assert.Equal(options.EffectiveDuration(timeline), result.Duration);
        Assert.Equal(expectedSourceFrameCount, result.SourceFrameCount);
        Assert.Equal(expectedAnalyzedFrameCount, result.Frames.Count);
        Assert.All(result.Frames, static frame => Assert.NotNull(frame.Diagnostics));
        Assert.All(result.Frames, static frame => Assert.True(frame.SourceWidth > 0 && frame.SourceHeight > 0));
        Assert.Contains(providerDiagnostics, static message =>
            message.StartsWith("ONNX Runtime provider:", StringComparison.Ordinal));
    }

    private void WriteFrameDetails(VideoAnalysisResult result)
    {
        foreach (var frame in result.Frames.Where(static item =>
                     item.Reads.Count > 0
                     || item.Confirmations.Count > 0
                     || item.Diagnostics?.Tracks.Count > 0))
        {
            var diagnostics = frame.Diagnostics!;
            output.WriteLine(
                $"frame={frame.SourceFrameIndex} position_ms={frame.Position.TotalMilliseconds:F0} " +
                $"total_ms={diagnostics.TotalMilliseconds:F1} detector_ms={diagnostics.Frame.Detector.TotalMilliseconds:F1} " +
                $"ocr_ms={diagnostics.Frame.Ocr.TotalMilliseconds:F1} tracking_ms={diagnostics.TrackingMilliseconds:F2} " +
                $"detections={diagnostics.Frame.DetectionCount} observations={diagnostics.Frame.ObservationCount} " +
                $"tracks={diagnostics.Tracks.Count} reads=[{string.Join(", ", frame.Reads.Select(static read => read.Text))}] " +
                $"confirmations=[{string.Join(", ", frame.Confirmations.Select(static confirmation => confirmation.DisplayPlate))}]");
        }
    }

    private static async Task WriteOptionalReportAsync(
        VideoAnalysisResult result,
        ReplaySummary summary,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        var reportPath = Environment.GetEnvironmentVariable(ReportEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(reportPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var report = new { Summary = summary, Analysis = result };
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(report, jsonOptions),
            cancellationToken);
    }

    private static int ReadPositiveInteger(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, out var parsed) || parsed < 1)
        {
            throw new InvalidOperationException($"{name} must be a positive integer.");
        }

        return parsed;
    }

    private sealed class LatestProgress<T> : IProgress<T>
    {
        public T? Value { get; private set; }

        public void Report(T value) => Value = value;
    }

    private sealed record ReplaySummary(
        string Source,
        double DurationSeconds,
        double SourceFramesPerSecond,
        int SourceFrameCount,
        int SamplingInterval,
        int ProcessedFrames,
        int SourceWidth,
        int SourceHeight,
        double WallMilliseconds,
        double AverageDecodeMilliseconds,
        double AverageRecognitionMilliseconds,
        TimingDistribution RecognitionTotalMilliseconds,
        TimingDistribution DetectorTotalMilliseconds,
        TimingDistribution DetectorInferenceMilliseconds,
        TimingDistribution OcrTotalMilliseconds,
        TimingDistribution OcrInferenceMilliseconds,
        TimingDistribution TrackingMilliseconds,
        int DetectionCandidates,
        int OcrAttempts,
        int Observations,
        int TrackCreations,
        int TrackAssociations,
        AssociationBreakdown AssociationsByKind,
        int MaximumConcurrentTracks,
        int Confirmations,
        IReadOnlyList<string> ConfirmedPlates,
        IReadOnlyList<ReadFrequency> MostFrequentReads,
        IReadOnlyList<string> Providers)
    {
        public static ReplaySummary Create(
            VideoAnalysisResult result,
            VideoAnalysisProgress? progress,
            double wallMilliseconds,
            IReadOnlyList<string> providers)
        {
            var diagnostics = result.Frames
                .Select(static frame => frame.Diagnostics)
                .OfType<RecognitionStreamDiagnostics>()
                .ToArray();
            var reads = result.Frames
                .SelectMany(static frame => frame.Reads)
                .Where(static read => !string.IsNullOrWhiteSpace(read.Text))
                .GroupBy(static read => read.Text, StringComparer.OrdinalIgnoreCase)
                .Select(static group => new ReadFrequency(
                    group.Key,
                    group.Count(),
                    group.Average(static read => read.OcrConfidence),
                    group.Average(static read => read.DetectorConfidence)))
                .OrderByDescending(static read => read.Count)
                .ThenByDescending(static read => read.AverageOcrConfidence)
                .Take(20)
                .ToArray();
            var confirmations = result.Frames
                .SelectMany(static frame => frame.Confirmations)
                .Select(static confirmation => confirmation.DisplayPlate)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new ReplaySummary(
                result.SourcePath,
                result.Duration.TotalSeconds,
                result.SourceFrameRate,
                result.SourceFrameCount,
                result.Sampling.Interval,
                result.Frames.Count,
                result.Frames.FirstOrDefault()?.SourceWidth ?? 0,
                result.Frames.FirstOrDefault()?.SourceHeight ?? 0,
                wallMilliseconds,
                progress?.AverageDecodeMilliseconds ?? 0,
                progress?.AverageRecognitionMilliseconds ?? 0,
                TimingDistribution.Create(diagnostics.Select(static item => item.TotalMilliseconds)),
                TimingDistribution.Create(diagnostics.Select(static item => item.Frame.Detector.TotalMilliseconds)),
                TimingDistribution.Create(diagnostics.Select(static item => item.Frame.Detector.InferenceMilliseconds)),
                TimingDistribution.Create(diagnostics.Select(static item => item.Frame.Ocr.TotalMilliseconds)),
                TimingDistribution.Create(diagnostics.Select(static item => item.Frame.Ocr.InferenceMilliseconds)),
                TimingDistribution.Create(diagnostics.Select(static item => item.TrackingMilliseconds)),
                diagnostics.Sum(static item => item.Frame.DetectionCount),
                diagnostics.Sum(static item => item.Frame.OcrAttemptCount),
                diagnostics.Sum(static item => item.Frame.ObservationCount),
                diagnostics.Sum(static item => item.Associations.Count(static association => association.Created)),
                diagnostics.Sum(static item => item.Associations.Count(static association => !association.Created)),
                AssociationBreakdown.Create(diagnostics.SelectMany(static item => item.Associations)),
                diagnostics.Length == 0 ? 0 : diagnostics.Max(static item => item.Tracks.Count),
                result.Frames.Sum(static frame => frame.Confirmations.Count),
                confirmations,
                reads,
                providers.ToArray());
        }
    }

    private sealed record ReadFrequency(
        string Text,
        int Count,
        double AverageOcrConfidence,
        double AverageDetectorConfidence);

    private sealed record AssociationBreakdown(
        int NewTrack,
        int ExactText,
        int SimilarText,
        int PredictedMotion,
        int Unspecified)
    {
        public static AssociationBreakdown Create(IEnumerable<PlateTrackAssociation> associations)
        {
            var counts = associations
                .GroupBy(static association => association.Kind)
                .ToDictionary(static group => group.Key, static group => group.Count());
            return new AssociationBreakdown(
                counts.GetValueOrDefault(PlateAssociationKind.NewTrack),
                counts.GetValueOrDefault(PlateAssociationKind.ExactText),
                counts.GetValueOrDefault(PlateAssociationKind.SimilarText),
                counts.GetValueOrDefault(PlateAssociationKind.PredictedMotion),
                counts.GetValueOrDefault(PlateAssociationKind.Unspecified));
        }
    }

    private sealed record TimingDistribution(double Mean, double Median, double P95)
    {
        public static TimingDistribution Create(IEnumerable<double> source)
        {
            var values = source.Order().ToArray();
            return values.Length == 0
                ? new TimingDistribution(0, 0, 0)
                : new TimingDistribution(values.Average(), Percentile(values, 0.5), Percentile(values, 0.95));
        }

        private static double Percentile(IReadOnlyList<double> values, double percentile)
        {
            var position = (values.Count - 1) * percentile;
            var lower = (int)Math.Floor(position);
            var upper = (int)Math.Ceiling(position);
            return lower == upper
                ? values[lower]
                : values[lower] + ((values[upper] - values[lower]) * (position - lower));
        }
    }
}

public sealed class LocalVideoFactAttribute : FactAttribute
{
    public LocalVideoFactAttribute()
    {
        var sourcePath = Environment.GetEnvironmentVariable("DEVEMOBILELPR_E2E_VIDEO");
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            Skip = "Set DEVEMOBILELPR_E2E_VIDEO to a local video file to run the real-video replay.";
        }
    }
}
