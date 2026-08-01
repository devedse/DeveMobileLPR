using System.Diagnostics;
using System.Text;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference.Models;
using DeveMobileLPR.Inference.Preprocessing;
using DeveMobileLPR.Recognition;
using Microsoft.ML.OnnxRuntime;

namespace DeveMobileLPR.Inference.Onnx;

public sealed class OnnxCctPlateRecognizer : IPlateRecognizer, IInferenceBackendInfo, IDisposable
{
    private static readonly long[] InputShape = [1, CctV2Metadata.Height, CctV2Metadata.Width, CctV2Metadata.Channels];
    private readonly InferenceSession _session;
    private readonly byte[] _input = new byte[CctV2Metadata.Width * CctV2Metadata.Height * CctV2Metadata.Channels];
    private readonly OrtValue _inputValue;
    private readonly Dictionary<string, OrtValue> _inputs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public OnnxCctPlateRecognizer(
        string modelPath,
        int xnnpackThreads = 2,
        Action<string>? diagnostic = null,
        bool allowNnapiFp16 = false)
    {
        var session = OnnxSessionFactory.Create(modelPath, xnnpackThreads, diagnostic, allowNnapiFp16);
        _session = session.Session;
        BackendName = session.BackendName;
        ValidateContract();
        _inputValue = OrtValue.CreateTensorValueFromMemory(_input, InputShape);
        _inputs = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
        {
            [_session.InputNames[0]] = _inputValue
        };
    }

    public string BackendName { get; }

    public async ValueTask<PlateRecognitionResult> RecognizeAsync(
        Yuv420Frame frame,
        BoundingBox plateBounds,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var queuedAt = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var queueMilliseconds = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
        try
        {
            var stageStartedAt = Stopwatch.GetTimestamp();
            var expanded = plateBounds.Expand(0.06f, 0.14f, frame.OrientedWidth, frame.OrientedHeight);
            OcrPreprocessor.Fill(frame, expanded, _input);
            var preprocessingMilliseconds = Stopwatch.GetElapsedTime(stageStartedAt).TotalMilliseconds;

            stageStartedAt = Stopwatch.GetTimestamp();
            using var runOptions = new RunOptions();
            using var outputs = _session.Run(runOptions, _inputs, _session.OutputNames);
            var inferenceMilliseconds = Stopwatch.GetElapsedTime(stageStartedAt).TotalMilliseconds;

            stageStartedAt = Stopwatch.GetTimestamp();
            OrtValue? plateOutput = null;
            OrtValue? regionOutput = null;
            for (var index = 0; index < _session.OutputNames.Count; index++)
            {
                if (string.Equals(_session.OutputNames[index], "plate", StringComparison.OrdinalIgnoreCase))
                {
                    plateOutput = outputs[index];
                }
                else if (string.Equals(_session.OutputNames[index], "region", StringComparison.OrdinalIgnoreCase))
                {
                    regionOutput = outputs[index];
                }
            }

            plateOutput ??= outputs[0];
            var read = Decode(plateOutput, regionOutput);
            var postprocessingMilliseconds = Stopwatch.GetElapsedTime(stageStartedAt).TotalMilliseconds;
            return new PlateRecognitionResult(
                read,
                new ModelExecutionTiming(
                    queueMilliseconds,
                    preprocessingMilliseconds,
                    inferenceMilliseconds,
                    postprocessingMilliseconds));
        }
        finally
        {
            _gate.Release();
        }
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
        _gate.Dispose();
    }

    private static PlateRead Decode(OrtValue plateOutput, OrtValue? regionOutput)
    {
        var values = plateOutput.GetTensorDataAsSpan<float>();
        var vocabularySize = CctV2Metadata.Alphabet.Length;
        var required = CctV2Metadata.MaximumSlots * vocabularySize;
        if (values.Length < required)
        {
            throw new InvalidDataException($"OCR plate output has {values.Length} values; expected at least {required}.");
        }

        var text = new StringBuilder(CctV2Metadata.MaximumSlots);
        var hypotheses = new List<CharacterHypothesis>(CctV2Metadata.MaximumSlots);
        var selectedProbabilities = new List<float>(CctV2Metadata.MaximumSlots);
        for (var slot = 0; slot < CctV2Metadata.MaximumSlots; slot++)
        {
            var slotValues = values.Slice(slot * vocabularySize, vocabularySize);
            var top = new CharacterCandidate[3];
            Array.Fill(top, new CharacterCandidate(CctV2Metadata.PaddingCharacter, float.NegativeInfinity));
            for (var index = 0; index < vocabularySize; index++)
            {
                var candidate = new CharacterCandidate(CctV2Metadata.Alphabet[index], slotValues[index]);
                for (var rank = 0; rank < top.Length; rank++)
                {
                    if (candidate.Probability <= top[rank].Probability)
                    {
                        continue;
                    }

                    for (var shift = top.Length - 1; shift > rank; shift--)
                    {
                        top[shift] = top[shift - 1];
                    }

                    top[rank] = candidate;
                    break;
                }
            }
            var best = top[0];
            if (best.Character == CctV2Metadata.PaddingCharacter)
            {
                break;
            }

            text.Append(best.Character);
            selectedProbabilities.Add(best.Probability);
            hypotheses.Add(new CharacterHypothesis(top.Where(static candidate => candidate.Character != CctV2Metadata.PaddingCharacter).ToArray()));
        }

        var confidence = selectedProbabilities.Count == 0 ? 0 : selectedProbabilities.Average();
        string? region = null;
        float? regionConfidence = null;
        if (regionOutput is not null)
        {
            var regionValues = regionOutput.GetTensorDataAsSpan<float>();
            if (regionValues.Length >= CctV2Metadata.Regions.Count)
            {
                var bestIndex = 0;
                for (var index = 1; index < CctV2Metadata.Regions.Count; index++)
                {
                    if (regionValues[index] > regionValues[bestIndex])
                    {
                        bestIndex = index;
                    }
                }

                region = CctV2Metadata.Regions[bestIndex];
                regionConfidence = regionValues[bestIndex];
            }
        }

        return new PlateRead(text.ToString(), confidence, hypotheses, region, regionConfidence);
    }

    private void ValidateContract()
    {
        var input = _session.InputMetadata.Single().Value;
        var dimensions = input.Dimensions;
        if (dimensions.Length != 4 || dimensions[1] != CctV2Metadata.Height || dimensions[2] != CctV2Metadata.Width || dimensions[3] != CctV2Metadata.Channels)
        {
            throw new InvalidDataException($"Expected OCR input [1,64,128,3], got [{string.Join(',', dimensions)}].");
        }
    }
}
