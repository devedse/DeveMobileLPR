using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference.Cct;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Inference.Onnx;

public sealed class OnnxCctPlateRecognizer : IPlateRecognizer, IInferenceBackendInfo, IInferenceBackendDiagnostics, IDisposable
{
    private readonly CctPlateRecognizer _recognizer;

    public OnnxCctPlateRecognizer(
        string modelPath,
        int xnnpackThreads = 2,
        Action<string>? diagnostic = null,
        bool allowNnapiFp16 = false)
    {
        _ = xnnpackThreads;
        _ = allowNnapiFp16;
        _recognizer = new CctPlateRecognizer(new OnnxCctRawModel(modelPath, diagnostic));
    }

    public string BackendName => _recognizer.BackendName;
    public IReadOnlyList<string> BackendDiagnostics => _recognizer.BackendDiagnostics;

    public ValueTask<PlateRecognitionResult> RecognizeAsync(
        Yuv420Frame frame,
        BoundingBox plateBounds,
        CancellationToken cancellationToken) =>
        _recognizer.RecognizeAsync(frame, plateBounds, cancellationToken);

    public void Dispose() => _recognizer.Dispose();
}