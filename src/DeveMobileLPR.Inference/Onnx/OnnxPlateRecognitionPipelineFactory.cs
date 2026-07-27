namespace DeveMobileLPR.Inference.Onnx;

public static class OnnxPlateRecognitionPipelineFactory
{
    public static PlateRecognitionPipeline Create(
        string detectorPath,
        string recognizerPath,
        Action<string>? diagnostic = null)
    {
        var detector = new OnnxYoloV9PlateDetector(detectorPath, diagnostic: diagnostic);
        try
        {
            var recognizer = new OnnxCctPlateRecognizer(recognizerPath, diagnostic: diagnostic);
            return new PlateRecognitionPipeline(detector, recognizer);
        }
        catch
        {
            detector.Dispose();
            throw;
        }
    }
}