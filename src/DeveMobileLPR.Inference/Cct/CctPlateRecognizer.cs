using System.Diagnostics;
using System.Text;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference.Models;
using DeveMobileLPR.Inference.Preprocessing;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Inference.Cct;

/// <summary>
/// Shared CCT-S V2 OCR pipeline. A platform-specific model runner supplies
/// fixed plate and region tensors; crop preprocessing and decoding are shared.
/// </summary>
public sealed class CctPlateRecognizer : IPlateRecognizer, IInferenceBackendInfo, IInferenceBackendDiagnostics, IDisposable
{
    public const int InputValueCount = CctV2Metadata.Width * CctV2Metadata.Height * CctV2Metadata.Channels;

    private readonly ICctRawModel _model;
    private readonly byte[] _input = new byte[InputValueCount];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public CctPlateRecognizer(ICctRawModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public string BackendName => _model.BackendName;
    public IReadOnlyList<string> BackendDiagnostics =>
        (_model as IInferenceBackendDiagnostics)?.BackendDiagnostics ?? [];

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
            ObjectDisposedException.ThrowIf(_disposed, this);
            var stageStartedAt = Stopwatch.GetTimestamp();
            var expanded = plateBounds.Expand(0.06f, 0.14f, frame.OrientedWidth, frame.OrientedHeight);
            OcrPreprocessor.Fill(frame, expanded, _input);
            var preprocessingMilliseconds = Stopwatch.GetElapsedTime(stageStartedAt).TotalMilliseconds;

            cancellationToken.ThrowIfCancellationRequested();
            stageStartedAt = Stopwatch.GetTimestamp();
            var output = _model.Run(_input);
            var inferenceMilliseconds = Stopwatch.GetElapsedTime(stageStartedAt).TotalMilliseconds;

            stageStartedAt = Stopwatch.GetTimestamp();
            var read = Decode(output.Plate, output.Region);
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
        _model.Dispose();
        _gate.Dispose();
    }

    internal static PlateRead Decode(ReadOnlySpan<float> plateValues, ReadOnlySpan<float> regionValues)
    {
        var vocabularySize = CctV2Metadata.Alphabet.Length;
        var required = CctV2Metadata.MaximumSlots * vocabularySize;
        if (plateValues.Length < required)
        {
            throw new InvalidDataException($"OCR plate output has {plateValues.Length} values; expected at least {required}.");
        }

        var text = new StringBuilder(CctV2Metadata.MaximumSlots);
        var hypotheses = new List<CharacterHypothesis>(CctV2Metadata.MaximumSlots);
        var selectedProbabilities = new List<float>(CctV2Metadata.MaximumSlots);
        for (var slot = 0; slot < CctV2Metadata.MaximumSlots; slot++)
        {
            var slotValues = plateValues.Slice(slot * vocabularySize, vocabularySize);
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

        return new PlateRead(text.ToString(), confidence, hypotheses, region, regionConfidence);
    }
}