using DeveMobileLPR.IO;

namespace DeveMobileLPR.Inference.Models;

public static class ModelArtifactVerifier
{
    public static async Task<bool> IsValidAsync(
        string path,
        ModelArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(artifact);
        if (!File.Exists(path) || new FileInfo(path).Length != artifact.Length)
        {
            return false;
        }

        var hash = await FileHash.Sha256Async(path, cancellationToken).ConfigureAwait(false);
        return hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task VerifyAsync(
        string path,
        ModelArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Model file is missing: {artifact.FileName}", path);
        }

        if (!await IsValidAsync(path, artifact, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException($"Model failed its integrity check: {artifact.FileName}");
        }
    }
}