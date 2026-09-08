using DeveMobileLPR.Application;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Inference.Cct;
using DeveMobileLPR.Inference.Models;
using DeveMobileLPR.Inference.Yolo;
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
        var detectorPath = await InstallAsync(ModelCatalog.LiteRtDetector, directory, cancellationToken).ConfigureAwait(false);
        var recognizerPath = await InstallAsync(ModelCatalog.LiteRtRecognizer, directory, cancellationToken).ConfigureAwait(false);
        var rawModel = new IosLiteRtYoloV9RawModel(detectorPath, diagnostic);
        YoloV9RawPlateDetector? detector = null;
        CctPlateRecognizer? recognizer = null;
        try
        {
            detector = new YoloV9RawPlateDetector(rawModel, recognitionTuning);
            recognizer = new CctPlateRecognizer(new IosLiteRtCctRawModel(recognizerPath, diagnostic));
            diagnostic?.Invoke($"Detector backend selected: {rawModel.BackendName}");
            return new PlateRecognitionPipeline(detector, recognizer, recognitionTuning);
        }
        catch
        {
            recognizer?.Dispose();
            if (detector is null)
            {
                rawModel.Dispose();
            }
            else
            {
                detector.Dispose();
            }
            throw;
        }
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
