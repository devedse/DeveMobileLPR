using DeveMobileLPR.Inference.Cct;
using DeveMobileLPR.Inference.Models;
using DeveMobileLPR.Recognition;
using Microsoft.ML.OnnxRuntime;

namespace DeveMobileLPR.Inference.Onnx;

public sealed class OnnxCctRawModel : ICctRawModel, IInferenceBackendDiagnostics
{
    private static readonly long[] InputShape = [1, CctV2Metadata.Height, CctV2Metadata.Width, CctV2Metadata.Channels];
    private readonly InferenceSession _session;
    private readonly byte[] _input = new byte[CctPlateRecognizer.InputValueCount];
    private readonly OrtValue _inputValue;
    private readonly Dictionary<string, OrtValue> _inputs;
    private readonly int _plateOutputIndex;
    private readonly int? _regionOutputIndex;
    private bool _disposed;

    public OnnxCctRawModel(
        string modelPath,
        Action<string>? diagnostic = null)
    {
        var session = OnnxSessionFactory.Create(modelPath, diagnostic);
        _session = session.Session;
        BackendName = session.BackendName;
        BackendDiagnostics = session.Diagnostics;
        ValidateContract();

        _inputValue = OrtValue.CreateTensorValueFromMemory(_input, InputShape);
        _inputs = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
        {
            [_session.InputNames[0]] = _inputValue
        };
        _plateOutputIndex = FindOutputIndex("plate") ?? 0;
        _regionOutputIndex = FindOutputIndex("region");
    }

    public string BackendName { get; }
    public IReadOnlyList<string> BackendDiagnostics { get; }

    public CctRawModelOutput Run(byte[] input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length != CctPlateRecognizer.InputValueCount)
        {
            throw new ArgumentException($"Expected {CctPlateRecognizer.InputValueCount} OCR input values.", nameof(input));
        }

        input.CopyTo(_input, 0);
        using var runOptions = new RunOptions();
        using var outputs = _session.Run(runOptions, _inputs, _session.OutputNames);
        return new CctRawModelOutput(
            outputs[_plateOutputIndex].GetTensorDataAsSpan<float>().ToArray(),
            _regionOutputIndex is int regionIndex
                ? outputs[regionIndex].GetTensorDataAsSpan<float>().ToArray()
                : null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inputValue.Dispose();
        _session.Dispose();
    }

    private int? FindOutputIndex(string name)
    {
        for (var index = 0; index < _session.OutputNames.Count; index++)
        {
            if (string.Equals(_session.OutputNames[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return null;
    }

    private void ValidateContract()
    {
        var input = _session.InputMetadata.Single().Value;
        var dimensions = input.Dimensions;
        if (dimensions.Length != 4
            || dimensions[1] != CctV2Metadata.Height
            || dimensions[2] != CctV2Metadata.Width
            || dimensions[3] != CctV2Metadata.Channels)
        {
            throw new InvalidDataException($"Expected OCR input [1,64,128,3], got [{string.Join(',', dimensions)}].");
        }
    }
}