namespace DeveMobileLPR.Inference.Models;

public sealed record ModelArtifact(
    string FileName,
    Uri DownloadUri,
    string Sha256,
    long Length,
    string Project,
    Uri LicenseUri);

public static class ModelCatalog
{
    public static ModelArtifact Detector { get; } = new(
        "yolo-v9-s-608-license-plates-end2end.onnx",
        new Uri("https://github.com/ankandrew/open-image-models/releases/download/assets/yolo-v9-s-608-license-plates-end2end.onnx"),
        "2B878B38D9AA07B6DDC3EA75C4FFCB39869BC5C218E0A14002F60AB2F7B0BE9A",
        28_612_350,
        "open-image-models",
        new Uri("https://github.com/ankandrew/open-image-models/blob/main/LICENSE"));

    public static ModelArtifact Recognizer { get; } = new(
        "cct_s_v2_global.onnx",
        new Uri("https://github.com/ankandrew/cnn-ocr-lp/releases/download/arg-plates/cct_s_v2_global.onnx"),
        "384BBBD2CEA3EF54761D3DF70822EF3A349EE1A112AEAFDDBE0E3BA06BC6E47B",
        5_262_230,
        "fast-plate-ocr",
        new Uri("https://github.com/ankandrew/fast-plate-ocr/blob/main/LICENSE"));

    public static IReadOnlyList<ModelArtifact> All { get; } = [Detector, Recognizer];
}
