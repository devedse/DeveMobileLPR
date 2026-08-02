using System.Buffers;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference.Cct;
using DeveMobileLPR.Inference.Models;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Tests;

public sealed class CctPlateRecognizerTests
{
    [Fact]
    public async Task RecognizeAsync_UsesSharedPreprocessingAndDecoding()
    {
        using var frame = CreateFrame();
        var model = new FakeRawModel();
        var recognizer = new CctPlateRecognizer(model);

        var result = await recognizer.RecognizeAsync(
            frame,
            new BoundingBox(10, 10, 150, 70),
            CancellationToken.None);

        Assert.Equal("AB12", result.Read.Text);
        Assert.Equal(0.9f, result.Read.Confidence, 5);
        Assert.Equal("Netherlands", result.Read.Region);
        Assert.Equal(0.8f, result.Read.RegionConfidence);
        Assert.Equal("Test raw OCR", recognizer.BackendName);
        Assert.Equal(["Test raw OCR initialized"], recognizer.BackendDiagnostics);
        Assert.Equal(CctPlateRecognizer.InputValueCount, model.InputLength);

        recognizer.Dispose();
        Assert.True(model.IsDisposed);
    }

    private static Yuv420Frame CreateFrame()
    {
        const int width = 160;
        const int height = 90;
        var y = MemoryPool<byte>.Shared.Rent(width * height);
        var u = MemoryPool<byte>.Shared.Rent(width * height / 4);
        var v = MemoryPool<byte>.Shared.Rent(width * height / 4);
        return new Yuv420Frame(
            1,
            DateTimeOffset.UnixEpoch,
            width,
            height,
            0,
            y,
            width * height,
            width,
            1,
            u,
            width * height / 4,
            width / 2,
            1,
            v,
            width * height / 4,
            width / 2,
            1);
    }

    private sealed class FakeRawModel : ICctRawModel, IInferenceBackendDiagnostics
    {
        private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_";

        public string BackendName => "Test raw OCR";
        public IReadOnlyList<string> BackendDiagnostics => ["Test raw OCR initialized"];
        public int InputLength { get; private set; }
        public bool IsDisposed { get; private set; }

        public CctRawModelOutput Run(byte[] input)
        {
            InputLength = input.Length;
            var plate = new float[10 * Alphabet.Length];
            SetCharacter(plate, 0, 'A', 0.9f);
            SetCharacter(plate, 1, 'B', 0.9f);
            SetCharacter(plate, 2, '1', 0.9f);
            SetCharacter(plate, 3, '2', 0.9f);
            SetCharacter(plate, 4, '_', 1f);

            var region = new float[66];
            region[43] = 0.8f;
            return new CctRawModelOutput(plate, region);
        }

        public void Dispose() => IsDisposed = true;

        private static void SetCharacter(float[] plate, int slot, char character, float probability)
        {
            plate[(slot * Alphabet.Length) + Alphabet.IndexOf(character)] = probability;
        }
    }
}