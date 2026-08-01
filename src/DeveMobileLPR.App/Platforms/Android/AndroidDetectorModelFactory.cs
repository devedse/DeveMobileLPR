using DeveMobileLPR.Inference.Models;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Inference.Yolo;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.Services;

/// <summary>
/// Selects the detector runtime at build time so comparison APKs exercise the
/// same preprocessing and postprocessing pipeline with only the model runner changed.
/// </summary>
internal static class AndroidDetectorModelFactory
{
#if ANDROID_ONNX_RAW_DETECTOR
    public static ModelArtifact Artifact => ModelCatalog.AndroidOnnxRawDetector;

    public static IYoloV9RawModel Create(
        string modelPath,
        RecognitionTuningConfiguration configuration,
        Action<string>? diagnostic) =>
        new OnnxYoloV9RawModel(
            modelPath,
            configuration.Detector_XnnpackThreads,
            diagnostic,
            configuration.Detector_AndroidAllowNnapiFp16);
#else
    public static ModelArtifact Artifact => ModelCatalog.AndroidLiteRtDetector;

    public static IYoloV9RawModel Create(
        string modelPath,
        RecognitionTuningConfiguration configuration,
        Action<string>? diagnostic) =>
        new AndroidLiteRtYoloV9RawModel(modelPath, diagnostic);
#endif
}
