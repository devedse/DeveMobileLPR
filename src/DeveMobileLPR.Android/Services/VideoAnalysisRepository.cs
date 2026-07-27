using System.Text.Json;

namespace DeveMobileLPR.AndroidApp.Services;

internal sealed class VideoAnalysisRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;

    public VideoAnalysisRepository(string directory)
    {
        _directory = directory;
    }

    public async Task SaveAsync(VideoAnalysisResult result, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var path = GetPath(result.Id);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await JsonSerializer.SerializeAsync(stream, result, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public async Task<IReadOnlyList<VideoAnalysisResult>> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var results = new List<VideoAnalysisResult>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
                var result = await JsonSerializer.DeserializeAsync<VideoAnalysisResult>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
                if (result is not null)
                {
                    results.Add(result);
                }
            }
            catch (JsonException)
            {
                // Keep one damaged entry from hiding the rest of the analysis library.
            }
        }

        return results.OrderByDescending(static result => result.AnalyzedAt).ToArray();
    }

    private string GetPath(Guid id) => Path.Combine(_directory, $"{id:N}.json");
}