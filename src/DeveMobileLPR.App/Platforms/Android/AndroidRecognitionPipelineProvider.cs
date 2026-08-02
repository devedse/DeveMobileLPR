using DeveMobileLPR.Application;
using DeveMobileLPR.App.Infrastructure;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Inference.Yolo;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.Services;

internal sealed class AndroidRecognitionPipelineProvider(
    RecognitionTuningConfiguration recognitionTuning,
    IDriveSettings settings) : IRecognitionPipelineProvider
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
            AndroidDetectorModelFactory.Artifact,
            cancellationToken).ConfigureAwait(false);
        var rawModel = AndroidDetectorModelFactory.Create(
            models.Detector,
            recognitionTuning,
            diagnostic,
            settings.RecognitionDebugEnabled,
            FileSystem.CacheDirectory);
        YoloV9RawPlateDetector? detector = null;
        try
        {
            detector = new YoloV9RawPlateDetector(rawModel, recognitionTuning);
            var recognizer = new OnnxCctPlateRecognizer(
                models.Ocr,
                recognitionTuning.Ocr_XnnpackThreads,
                diagnostic,
                recognitionTuning.Ocr_AndroidAllowNnapiFp16);
            diagnostic?.Invoke($"Detector backend selected: {rawModel.BackendName}");
            return new PlateRecognitionPipeline(detector, recognizer, recognitionTuning);
        }
        catch
        {
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
