using DeveMobileLPR.Application;
using DeveMobileLPR.Inference.Models;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.Platforms.Windows.Inference;

internal sealed class WindowsRecognitionPipelineProvider(
    RecognitionTuningConfiguration recognitionTuning) : IRecognitionPipelineProvider
{
    public async Task<IFrameRecognitionPipeline> CreateAsync(
        Action<string>? diagnostic,
        CancellationToken cancellationToken)
    {
        var modelDirectory = Path.Combine(AppContext.BaseDirectory, "models");
        var detectorPath = Path.Combine(modelDirectory, ModelCatalog.Detector.FileName);
        var recognizerPath = Path.Combine(modelDirectory, ModelCatalog.Recognizer.FileName);
        await ModelArtifactVerifier.VerifyAsync(detectorPath, ModelCatalog.Detector, cancellationToken).ConfigureAwait(false);
        await ModelArtifactVerifier.VerifyAsync(recognizerPath, ModelCatalog.Recognizer, cancellationToken).ConfigureAwait(false);
        return OnnxPlateRecognitionPipelineFactory.Create(
            detectorPath,
            recognizerPath,
            diagnostic,
            recognitionTuning);
    }
}
