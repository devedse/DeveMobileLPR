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
                [new AnalyzedPlateRead("AB1234", 0.95f, 0.9f)],
                [new AnalyzedPlateConfirmation("AB1234", "AB-12-34", 0.95f, 3)])
        ]);
}