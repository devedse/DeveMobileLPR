using DeveMobileLPR.Inference.Yolo;

namespace DeveMobileLPR.App.Services;

/// <summary>
/// Runs the same fixed-output LiteRT detector graph used by Android. Metal is
/// selected only after a full warm inference; a separate CPU interpreter is
/// created when Metal cannot execute the model.
/// </summary>
internal sealed class IosLiteRtYoloV9RawModel : IYoloV9RawModel
{
    private const int CandidateCount = 7_581;
    private const int BoxCoordinateCount = 4;
    private static readonly TfLiteTensorContract InputContract = new(
        TfLiteType.Float32, 1, 608, 608, 3);
    private static readonly TfLiteTensorContract[] OutputContracts =
    [
        new(TfLiteType.Float32, 1, CandidateCount, BoxCoordinateCount),
        new(TfLiteType.Float32, 1, CandidateCount, 1)
    ];

    private readonly IosLiteRtSession _session;
    private readonly int _boxesOutputIndex;
    private readonly int _scoresOutputIndex;
    private bool _disposed;

    public IosLiteRtYoloV9RawModel(string modelPath, Action<string>? diagnostic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("LiteRT detector model is missing.", modelPath);
        }

        var created = TryCreate(modelPath, useMetal: true, "Metal GPU", diagnostic)
            ?? TryCreate(modelPath, useMetal: false, "CPU", diagnostic)
            ?? throw new InvalidOperationException("LiteRT could not initialize the detector on Metal or CPU.");
        _session = created.Session;
        _boxesOutputIndex = created.Boxes;
        _scoresOutputIndex = created.Scores;
        BackendName = $"LiteRT {_session.AcceleratorName}";
    }

    public string BackendName { get; }
    public YoloV9InputLayout InputLayout => YoloV9InputLayout.ChannelsLast;

    public YoloV9RawModelOutput Run(float[] input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length != YoloV9RawPlateDetector.InputValueCount)
        {
            throw new ArgumentException(
                $"Expected {YoloV9RawPlateDetector.InputValueCount} detector input values.",
                nameof(input));
        }

        _session.CopyInput(input);
        _session.Invoke();
        return new YoloV9RawModelOutput(
            _session.ReadFloatOutput(_boxesOutputIndex),
            _session.ReadFloatOutput(_scoresOutputIndex));
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
            session.CopyInput(new float[YoloV9RawPlateDetector.InputValueCount]);
            session.Invoke();
            var first = session.ReadFloatOutput(0);
            var second = session.ReadFloatOutput(1);
            var (boxes, scores) = IdentifyOutputs(first, second);
            diagnostic?.Invoke(
                $"LiteRT detector selected {acceleratorName}; tensor validation and a full warm inference succeeded.");
            var result = new CreatedSession(session, boxes, scores);
            session = null;
            return result;
        }
        catch (Exception exception)
        {
            diagnostic?.Invoke(
                $"LiteRT detector rejected {acceleratorName}: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
        finally
        {
            session?.Dispose();
        }
    }

    private static (int Boxes, int Scores) IdentifyOutputs(float[] first, float[] second)
    {
        var boxesLength = CandidateCount * BoxCoordinateCount;
        if (first.Length == boxesLength && second.Length == CandidateCount)
        {
            return (0, 1);
        }
        if (second.Length == boxesLength && first.Length == CandidateCount)
        {
            return (1, 0);
        }

        throw new InvalidDataException(
            $"Unexpected LiteRT detector outputs {first.Length} and {second.Length}.");
    }

    private readonly record struct CreatedSession(IosLiteRtSession Session, int Boxes, int Scores);
}
