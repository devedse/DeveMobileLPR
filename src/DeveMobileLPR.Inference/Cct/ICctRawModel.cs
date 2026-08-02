namespace DeveMobileLPR.Inference.Cct;

/// <summary>
/// Platform runtime boundary for the fixed-output CCT-S V2 OCR graph.
/// Implementations execute the model only; crop preprocessing and decoding
/// remain shared.
/// </summary>
public interface ICctRawModel : IDisposable
{
    string BackendName { get; }

    CctRawModelOutput Run(byte[] input);
}

public readonly record struct CctRawModelOutput(
    float[] Plate,
    float[]? Region);