using DeveMobileLPR.Application;
using DeveMobileLPR.Inference.Models;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.Services;

internal sealed class IosRecognitionPipelineProvider(
    RecognitionTuningConfiguration recognitionTuning) : IRecognitionPipelineProvider
{
    public async Task<IFrameRecognitionPipeline> CreateAsync(
        Action<string>? diagnostic,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(FileSystem.AppDataDirectory, "models");
        var detector = await InstallAsync(ModelCatalog.Detector, directory, cancellationToken).ConfigureAwait(false);
        var recognizer = await InstallAsync(ModelCatalog.Recognizer, directory, cancellationToken).ConfigureAwait(false);
        return OnnxPlateRecognitionPipelineFactory.Create(
            detector,
            recognizer,
            diagnostic,
            recognitionTuning);
    }

    private static async Task<string> InstallAsync(
        ModelArtifact artifact,
        string directory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, artifact.FileName);
        if (await IsValidAsync(destination, artifact, cancellationToken).ConfigureAwait(false))
        {
            return destination;
        }

        var temporary = destination + ".installing";
        try
        {
            await using var source = await FileSystem.OpenAppPackageFileAsync($"models/{artifact.FileName}").ConfigureAwait(false);
            await using (var output = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            await ModelArtifactVerifier.VerifyAsync(temporary, artifact, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<bool> IsValidAsync(
        string path,
        ModelArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        try
        {
            await ModelArtifactVerifier.VerifyAsync(path, artifact, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
