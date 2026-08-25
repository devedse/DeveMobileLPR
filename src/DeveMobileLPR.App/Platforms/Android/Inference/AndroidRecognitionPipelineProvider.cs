using DeveMobileLPR.Application;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Inference.Cct;
using DeveMobileLPR.Inference.Models;
using DeveMobileLPR.Inference.Yolo;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.Platforms.Android.Inference;

internal sealed class AndroidRecognitionPipelineProvider(
    RecognitionTuningConfiguration recognitionTuning,
    InferenceBackendStatus backendStatus) : IRecognitionPipelineProvider
{
    public async Task<IFrameRecognitionPipeline> CreateAsync(
        Action<string>? diagnostic,
        CancellationToken cancellationToken)
    {
        backendStatus.ReportInitializing("trying LiteRT NPU");
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
        AndroidLiteRtCctRawModel? rawOcrModel = null;
        CctPlateRecognizer? recognizer = null;
        try
        {
            detector = new YoloV9RawPlateDetector(rawModel, recognitionTuning);
            rawOcrModel = new AndroidLiteRtCctRawModel(models.Ocr, diagnostic);
            recognizer = new CctPlateRecognizer(rawOcrModel);
            diagnostic?.Invoke($"Detector backend selected: {rawModel.BackendName}");
            diagnostic?.Invoke($"OCR backend selected: {rawOcrModel.BackendName}");
            var npuActive = rawModel.BackendName.EndsWith("NPU", StringComparison.Ordinal)
                && rawOcrModel.BackendName.EndsWith("NPU", StringComparison.Ordinal);
            backendStatus.ReportSelected(
                rawModel.BackendName,
                rawOcrModel.BackendName,
                npuActive ? "NPU active" : "NPU unavailable for one or more models · fallback active");
            return new PlateRecognitionPipeline(detector, recognizer, recognitionTuning);
        }
        catch (Exception exception)
        {
            backendStatus.ReportFailure(exception.Message);
            recognizer?.Dispose();
            if (recognizer is null)
            {
                rawOcrModel?.Dispose();
            }
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
