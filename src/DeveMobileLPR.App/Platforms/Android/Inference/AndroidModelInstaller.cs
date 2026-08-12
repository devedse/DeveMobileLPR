using Android.Content.Res;
using DeveMobileLPR.Inference.Models;

namespace DeveMobileLPR.App.Platforms.Android.Inference;

internal static class AndroidModelInstaller
{
    public static async Task<(string Detector, string Ocr)> EnsureInstalledAsync(
        AssetManager assets,
        string filesDirectory,
        ModelArtifact detectorArtifact,
        ModelArtifact ocrArtifact,
        CancellationToken cancellationToken)
    {
        var modelDirectory = Path.Combine(filesDirectory, "models");
        Directory.CreateDirectory(modelDirectory);
        var detector = await CopyVerifiedAsync(assets, detectorArtifact, modelDirectory, cancellationToken);
        var ocr = await CopyVerifiedAsync(assets, ocrArtifact, modelDirectory, cancellationToken);
        return (detector, ocr);
    }

    private static async Task<string> CopyVerifiedAsync(
        AssetManager assets,
        ModelArtifact artifact,
        string modelDirectory,
        CancellationToken cancellationToken)
    {
        var target = Path.Combine(modelDirectory, artifact.FileName);
        if (await ModelArtifactVerifier.IsValidAsync(target, artifact, cancellationToken).ConfigureAwait(false))
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

        if (!await ModelArtifactVerifier.IsValidAsync(temporary, artifact, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(temporary);
            throw new InvalidDataException($"Bundled model failed its integrity check: {artifact.FileName}");
        }

        File.Move(temporary, target, true);
        return target;
    }
}
