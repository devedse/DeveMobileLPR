using DeveMobileLPR.Inference.Yolo;
using DeveMobileLPR.Recognition;
using Microsoft.ML.OnnxRuntime;

namespace DeveMobileLPR.Inference.Onnx;

/// <summary>
/// Runs the pre-NMS YOLO graph through ONNX Runtime. This provides a direct
/// comparison with LiteRT while reusing identical preprocessing and C# NMS.
/// </summary>
public sealed class OnnxYoloV9RawModel : IYoloV9RawModel, IInferenceBackendDiagnostics
{
    private static readonly long[] InputShape = [1, 3, 608, 608];
    private const int CandidateCount = 7_581;
    private const int BoxCoordinateCount = 4;

    private readonly InferenceSession _session;
    private readonly float[] _input = new float[YoloV9RawPlateDetector.InputValueCount];
    private readonly OrtValue _inputValue;
    private readonly Dictionary<string, OrtValue> _inputs;
    private readonly string _boxesOutputName;
    private readonly string _scoresOutputName;
    private bool _disposed;

    public OnnxYoloV9RawModel(
        string modelPath,
        int xnnpackThreads = 4,
        Action<string>? diagnostic = null,
        bool allowNnapiFp16 = false,
        OnnxExecutionProviderConfiguration? preferredAndroidProvider = null)
    {
        var session = OnnxSessionFactory.Create(
            modelPath,
            xnnpackThreads,
            diagnostic,
            allowNnapiFp16,
            preferredAndroidProvider);
        _session = session.Session;
        BackendName = session.BackendName;
        BackendDiagnostics = session.Diagnostics;
        try
        {
            ValidateContract(out _boxesOutputName, out _scoresOutputName);
            _inputValue = OrtValue.CreateTensorValueFromMemory(_input, InputShape);
            _inputs = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
            {
                [_session.InputNames.Single()] = _inputValue
            };
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    public string BackendName { get; }
    public IReadOnlyList<string> BackendDiagnostics { get; }
    public YoloV9InputLayout InputLayout => YoloV9InputLayout.ChannelsFirst;

    public YoloV9RawModelOutput Run(float[] input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length != _input.Length)
        {
            throw new ArgumentException($"Expected {_input.Length} detector input values.", nameof(input));
        }

        input.CopyTo(_input, 0);
        using var runOptions = new RunOptions();
        using var outputs = _session.Run(runOptions, _inputs, [_boxesOutputName, _scoresOutputName]);
        return new YoloV9RawModelOutput(
            outputs[0].GetTensorDataAsSpan<float>().ToArray(),
            outputs[1].GetTensorDataAsSpan<float>().ToArray());
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

    private void ValidateContract(out string boxesOutputName, out string scoresOutputName)
    {
        var input = _session.InputMetadata.Single().Value;
        if (!input.Dimensions.SequenceEqual([1, 3, 608, 608]))
        {
            throw new InvalidDataException(
                $"Expected raw detector input [1,3,608,608], got [{string.Join(',', input.Dimensions)}].");
        }

        boxesOutputName = FindOutputName([1, CandidateCount, BoxCoordinateCount]);
        scoresOutputName = FindOutputName([1, CandidateCount, 1]);
    }

    private string FindOutputName(int[] expectedShape)
    {
        var matches = _session.OutputMetadata
            .Where(item => item.Value.Dimensions.SequenceEqual(expectedShape))
            .Select(static item => item.Key)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"Expected one raw detector output [{string.Join(',', expectedShape)}], found {matches.Length}.");
    }
}
