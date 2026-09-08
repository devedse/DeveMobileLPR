using DeveMobileLPR.Inference.Cct;

namespace DeveMobileLPR.App.Services;

/// <summary>
/// Runs the same fixed-output LiteRT OCR graph used by Android with a proven
/// Metal session and an explicit CPU fallback.
/// </summary>
internal sealed class IosLiteRtCctRawModel : ICctRawModel
{
    private const int PlateOutputValueCount = 10 * 37;
    private const int RegionOutputValueCount = 66;
    private static readonly TfLiteTensorContract InputContract = new(
        TfLiteType.UInt8, 1, 64, 128, 3);
    private static readonly TfLiteTensorContract[] OutputContracts =
    [
        new(TfLiteType.Float32, 1, 10, 37),
        new(TfLiteType.Float32, 1, RegionOutputValueCount)
    ];

    private readonly IosLiteRtSession _session;
    private readonly int _plateOutputIndex;
    private readonly int _regionOutputIndex;
    private bool _disposed;

    public IosLiteRtCctRawModel(string modelPath, Action<string>? diagnostic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("LiteRT OCR model is missing.", modelPath);
        }

        var created = TryCreate(modelPath, useMetal: true, "Metal GPU", diagnostic)
            ?? TryCreate(modelPath, useMetal: false, "CPU", diagnostic)
            ?? throw new InvalidOperationException("LiteRT could not initialize OCR on Metal or CPU.");
        _session = created.Session;
        _plateOutputIndex = created.Plate;
        _regionOutputIndex = created.Region;
        BackendName = $"LiteRT {_session.AcceleratorName}";
    }

    public string BackendName { get; }

    public CctRawModelOutput Run(byte[] input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length != CctPlateRecognizer.InputValueCount)
        {
            throw new ArgumentException(
                $"Expected {CctPlateRecognizer.InputValueCount} OCR input values.",
                nameof(input));
        }

        _session.CopyInput(input);
        _session.Invoke();
        return new CctRawModelOutput(
            _session.ReadFloatOutput(_plateOutputIndex),
            _session.ReadFloatOutput(_regionOutputIndex));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
    }

    private static CreatedSession? TryCreate(
        string modelPath,
        bool useMetal,
        string acceleratorName,
        Action<string>? diagnostic)
    {
        IosLiteRtSession? session = null;
        try
        {
            session = IosLiteRtSession.Create(modelPath, useMetal, InputContract, OutputContracts);
            session.CopyInput(new byte[CctPlateRecognizer.InputValueCount]);
            session.Invoke();
            var first = session.ReadFloatOutput(0);
            var second = session.ReadFloatOutput(1);
            var (plate, region) = IdentifyOutputs(first, second);
            diagnostic?.Invoke(
                $"LiteRT OCR selected {acceleratorName}; tensor validation and a full warm inference succeeded.");
            var result = new CreatedSession(session, plate, region);
            session = null;
            return result;
        }
        catch (Exception exception)
        {
            diagnostic?.Invoke(
                $"LiteRT OCR rejected {acceleratorName}: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
        finally
        {
            session?.Dispose();
        }
    }

    private static (int Plate, int Region) IdentifyOutputs(float[] first, float[] second)
    {
        if (first.Length == PlateOutputValueCount && second.Length == RegionOutputValueCount)
        {
            return (0, 1);
        }
        if (second.Length == PlateOutputValueCount && first.Length == RegionOutputValueCount)
        {
            return (1, 0);
        }

        throw new InvalidDataException($"Unexpected LiteRT OCR outputs {first.Length} and {second.Length}.");
    }

    private readonly record struct CreatedSession(IosLiteRtSession Session, int Plate, int Region);
}
