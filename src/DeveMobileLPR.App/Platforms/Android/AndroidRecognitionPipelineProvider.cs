using DeveMobileLPR.Application;
using DeveMobileLPR.App.Infrastructure;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.Services;

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
            cancellationToken).ConfigureAwait(false);
        return OnnxPlateRecognitionPipelineFactory.Create(
            models.Detector,
            models.Ocr,
            diagnostic,
            recognitionTuning);
    }
}
