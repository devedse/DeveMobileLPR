using DeveMobileLPR.Application;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Inference.Models;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.Platforms.Windows.Inference;

internal sealed class WindowsRecognitionPipelineProvider(
    RecognitionTuningConfiguration recognitionTuning,
    InferenceBackendStatus backendStatus) : IRecognitionPipelineProvider
{
    public async Task<IFrameRecognitionPipeline> CreateAsync(
        Action<string>? diagnostic,
        CancellationToken cancellationToken)
    {
        backendStatus.ReportInitializing("initializing Windows inference");
        var modelDirectory = Path.Combine(AppContext.BaseDirectory, "models");
        var detectorPath = Path.Combine(modelDirectory, ModelCatalog.Detector.FileName);
        var recognizerPath = Path.Combine(modelDirectory, ModelCatalog.Recognizer.FileName);
        await ModelArtifactVerifier.VerifyAsync(detectorPath, ModelCatalog.Detector, cancellationToken).ConfigureAwait(false);
        await ModelArtifactVerifier.VerifyAsync(recognizerPath, ModelCatalog.Recognizer, cancellationToken).ConfigureAwait(false);
        try
        {
            var pipeline = OnnxPlateRecognitionPipelineFactory.Create(
                detectorPath,
                recognizerPath,
                diagnostic,
                recognitionTuning);
            backendStatus.ReportSelected(pipeline.DetectorBackend, pipeline.OcrBackend);
            return pipeline;
        }
        catch (Exception exception)
        {
            backendStatus.ReportFailure(exception.Message);
            throw;
        }
    }
}
