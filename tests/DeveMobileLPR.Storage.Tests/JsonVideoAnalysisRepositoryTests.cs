using DeveMobileLPR.Geometry;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.Tests;

public sealed class JsonVideoAnalysisRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DeveMobileLPR-analysis-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveAndLoad_RoundTripsCompactResultsNewestFirst()
    {
        var repository = new JsonVideoAnalysisRepository(_directory);
        var older = CreateResult(DateTimeOffset.Parse("2026-07-26T08:00:00Z"), "older.mp4");
        var newer = CreateResult(DateTimeOffset.Parse("2026-07-27T08:00:00Z"), "newer.mp4");

        await repository.SaveAsync(older, CancellationToken.None);
        await repository.SaveAsync(newer, CancellationToken.None);

        var results = await repository.LoadAllAsync(CancellationToken.None);
        Assert.Equal([newer.Id, older.Id], results.Select(static result => result.Id));
        var frame = Assert.Single(results[0].Frames);
        Assert.Equal("AB1234", Assert.Single(frame.Reads).Text);
        Assert.Equal("AB-12-34", Assert.Single(frame.Confirmations).DisplayPlate);
        Assert.Equal(1280, frame.SourceWidth);
        Assert.Equal(720, frame.SourceHeight);
        Assert.Equal(new BoundingBox(100, 200, 300, 260), Assert.Single(frame.Reads).Bounds);
        Assert.Equal(new BoundingBox(105, 202, 298, 258), Assert.Single(frame.Confirmations).Bounds);
        Assert.Equal(42, frame.Diagnostics?.Frame.TotalMilliseconds);
        Assert.Equal(0.75, frame.Diagnostics?.Frame.CropQualityMilliseconds);
        Assert.Equal(3, Assert.Single(frame.Diagnostics!.Tracks).ObservationCount);
        Assert.Equal("AB1234", Assert.Single(frame.Diagnostics.Frame.Candidates).ReadText);
        var association = Assert.Single(frame.Diagnostics.Associations);
        Assert.Equal(PlateAssociationKind.PredictedMotion, association.Kind);
        Assert.Equal(new BoundingBox(110, 205, 303, 261), association.PredictedBounds);
        Assert.Equal(0.83f, association.Score);
        var json = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_directory, $"{newer.Id:N}.json")));
        Assert.DoesNotContain("preview", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("image", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pixels", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAll_SkipsMalformedEntries()
    {
        var repository = new JsonVideoAnalysisRepository(_directory);
        var valid = CreateResult(DateTimeOffset.UtcNow, "valid.mp4");
        await repository.SaveAsync(valid, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_directory, "damaged.json"), "{not-json");

        var result = Assert.Single(await repository.LoadAllAsync(CancellationToken.None));

        Assert.Equal(valid.Id, result.Id);
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlySelectedAnalysis()
    {
        var repository = new JsonVideoAnalysisRepository(_directory);
        var retained = CreateResult(DateTimeOffset.UtcNow, "retained.mp4");
        var deleted = CreateResult(DateTimeOffset.UtcNow, "deleted.mp4");
        await repository.SaveAsync(retained, CancellationToken.None);
        await repository.SaveAsync(deleted, CancellationToken.None);

        await repository.DeleteAsync(deleted.Id, CancellationToken.None);

        var result = Assert.Single(await repository.LoadAllAsync(CancellationToken.None));
        Assert.Equal(retained.Id, result.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static VideoAnalysisResult CreateResult(DateTimeOffset analyzedAt, string displayName) => new(
        Guid.NewGuid(),
        Path.Combine("videos", displayName),
        displayName,
        analyzedAt,
        TimeSpan.FromSeconds(10),
        30,
        300,
        new VideoFrameSampling(4),
        [
            new AnalyzedVideoFrame(
                40,
                TimeSpan.FromSeconds(2),
                [new AnalyzedPlateRead("AB1234", 0.95f, 0.9f, new BoundingBox(100, 200, 300, 260))],
                [new AnalyzedPlateConfirmation("AB1234", "AB-12-34", 0.95f, 3, new BoundingBox(105, 202, 298, 258))],
                1280,
                720)
            {
                Diagnostics = new RecognitionStreamDiagnostics(
                    new RecognitionFrameDiagnostics(
                        42,
                        new ModelExecutionTiming(0, 2, 20, 1),
                        new ModelExecutionTiming(0, 3, 14, 1),
                        1,
                        1,
                        1)
                    {
                        CropQualityMilliseconds = 0.75,
                        Candidates = [new PlateCandidateDiagnostics(
                            new PlateDetection(new BoundingBox(100, 200, 300, 260), 0.9f),
                            0.9f,
                            true,
                            "AB1234",
                            0.95f,
                            new ModelExecutionTiming(0, 3, 14, 1))]
                    },
                    1,
                    [new PlateTrackSnapshot(
                        Guid.NewGuid(),
                        analyzedAt,
                        analyzedAt,
                        new BoundingBox(100, 200, 300, 260),
                        3,
                        true,
                        40,
                        "AB1234",
                        0.9f,
                        0.95f,
                        0.9f)],
                    [new PlateTrackAssociation(
                        Guid.NewGuid(),
                        40,
                        false,
                        0.2f)
                    {
                        Kind = PlateAssociationKind.PredictedMotion,
                        PredictedBounds = new BoundingBox(110, 205, 303, 261),
                        PredictedIntersectionOverUnion = 0.65f,
                        FrameCenterDistance = 0.01f,
                        ScaleRatio = 1.05f,
                        TextEditDistance = 1,
                        Score = 0.83f
                    }])
            }
        ]);
}
