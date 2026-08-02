namespace DeveMobileLPR.Inference.Models;

public sealed record ModelArtifact(
    string FileName,
    string Sha256,
    long Length);

public static class ModelCatalog
{
    public static ModelArtifact Detector { get; } = new(
        "yolo-v9-s-608-license-plates-end2end.onnx",
        "2B878B38D9AA07B6DDC3EA75C4FFCB39869BC5C218E0A14002F60AB2F7B0BE9A",
        28_612_350);

    public static ModelArtifact Recognizer { get; } = new(
        "cct_s_v2_global.onnx",
        "384BBBD2CEA3EF54761D3DF70822EF3A349EE1A112AEAFDDBE0E3BA06BC6E47B",
        5_262_230);

    public static ModelArtifact AndroidLiteRtDetector { get; } = new(
        "yolo-v9-s-608-license-plates-raw_float32.tflite",
        "2D3CF7D206197A0BC719C25422254EFC255B81F9495825D6DBA5A7D770A39433",
        28_561_236);

    public static ModelArtifact AndroidOnnxRawDetector { get; } = new(
        "yolo-v9-s-608-license-plates-raw.onnx",
        "291F31E43FF4DA82C29168960AC12B672F9D57CA04A83594B87F8BAEA108B49F",
        28_608_857);
}
