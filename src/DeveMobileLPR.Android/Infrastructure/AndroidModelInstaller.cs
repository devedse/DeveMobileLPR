using Android.Content.Res;
using DeveMobileLPR.Inference.Models;

namespace DeveMobileLPR.AndroidApp.Infrastructure;

internal static class AndroidModelInstaller
{
    public static async Task<(string Detector, string Ocr)> EnsureInstalledAsync(
        AssetManager assets,
        string filesDirectory,
        CancellationToken cancellationToken)
    {
        var modelDirectory = Path.Combine(filesDirectory, "models");
        Directory.CreateDirectory(modelDirectory);
        var detector = await CopyVerifiedAsync(assets, ModelCatalog.Detector, modelDirectory, cancellationToken);
        var ocr = await CopyVerifiedAsync(assets, ModelCatalog.Recognizer, modelDirectory, cancellationToken);
        return (detector, ocr);
    }

    private static async Task<string> CopyVerifiedAsync(
        AssetManager assets,
        ModelArtifact artifact,
        string modelDirectory,
        CancellationToken cancellationToken)
    {
        var target = Path.Combine(modelDirectory, artifact.FileName);
        if (await IsValidAsync(target, artifact, cancellationToken).ConfigureAwait(false))
        {
            return target;
        }

        var temporary = target + ".installing";
        await using (var source = assets.Open($"models/{artifact.FileName}")
            ?? throw new FileNotFoundException($"Bundled model asset is missing: {artifact.FileName}"))
        await using (var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        if (!await IsValidAsync(temporary, artifact, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(temporary);
            throw new InvalidDataException($"Bundled model failed its integrity check: {artifact.FileName}");
        }

        File.Move(temporary, target, true);
        return target;
    }

    private static async Task<bool> IsValidAsync(string path, ModelArtifact artifact, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != artifact.Length)
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
