using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Inference.Onnx;

public static class OnnxPlateRecognitionPipelineFactory
{
    public static PlateRecognitionPipeline Create(
        string detectorPath,
        string recognizerPath,
        Action<string>? diagnostic = null,
        RecognitionTuningConfiguration? configuration = null)
    {
        configuration ??= new RecognitionTuningConfiguration();
        configuration.Validate();
        var detector = new OnnxYoloV9PlateDetector(detectorPath, configuration, diagnostic);
        try
        {
            var recognizer = new OnnxCctPlateRecognizer(recognizerPath, diagnostic: diagnostic);
            return new PlateRecognitionPipeline(detector, recognizer, configuration);
        }
        catch
        {
            detector.Dispose();
            throw;
        }
    }
}
