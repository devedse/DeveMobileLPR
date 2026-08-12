using DeveMobileLPR.Application;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Inference.Cct;
using DeveMobileLPR.Inference.Models;
using DeveMobileLPR.Inference.Yolo;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.Platforms.Android.Inference;

internal sealed class AndroidRecognitionPipelineProvider(
    RecognitionTuningConfiguration recognitionTuning) : IRecognitionPipelineProvider
{
    public async Task<IFrameRecognitionPipeline> CreateAsync(
        Action<string>? diagnostic,
        CancellationToken cancellationToken)
    {
        var context = global::Android.App.Application.Context;
        var files = context.FilesDir?.AbsolutePath ?? FileSystem.AppDataDirectory;
        var models = await AndroidModelInstaller.EnsureInstalledAsync(
            context.Assets ?? throw new InvalidOperationException("Application assets are unavailable."),
            files,
            ModelCatalog.AndroidLiteRtDetector,
            ModelCatalog.AndroidLiteRtRecognizer,
            cancellationToken).ConfigureAwait(false);
        var rawModel = new AndroidLiteRtYoloV9RawModel(models.Detector, diagnostic);
        YoloV9RawPlateDetector? detector = null;
        CctPlateRecognizer? recognizer = null;
        try
        {
            detector = new YoloV9RawPlateDetector(rawModel, recognitionTuning);
            recognizer = new CctPlateRecognizer(new AndroidLiteRtCctRawModel(models.Ocr, diagnostic));
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
}
