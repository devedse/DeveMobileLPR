using System.Buffers;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Inference.Yolo;

namespace DeveMobileLPR.Tests;

public sealed class OnnxModelContractTests
{
    [Fact]
    [Trait("Category", "Model")]
    public async Task SelectedModels_LoadAndRunWithTheirPinnedContracts()
    {
        var modelDirectory = FindModelDirectory();
        using var detector = new OnnxYoloV9PlateDetector(Path.Combine(modelDirectory, "yolo-v9-s-608-license-plates-end2end.onnx"));
        using var recognizer = new OnnxCctPlateRecognizer(Path.Combine(modelDirectory, "cct_s_v2_global.onnx"));
        using var frame = CreateFrame(640, 360);

        var detections = await detector.DetectAsync(frame, CancellationToken.None);
        var read = await recognizer.RecognizeAsync(
            frame,
            new DeveMobileLPR.Geometry.BoundingBox(100, 100, 300, 180),
            CancellationToken.None);

        Assert.NotNull(detections.Detections);
        Assert.True(detections.Timing.TotalMilliseconds > 0);
        Assert.NotNull(read.Read.Text);
        Assert.InRange(read.Read.Confidence, 0, 1);
        Assert.True(read.Timing.TotalMilliseconds > 0);
    }

    [Fact]
    [Trait("Category", "Model")]
    [Trait("GeneratedModel", "true")]
    public void RawDetectorModel_LoadsAndReturnsPreNmsBoxesAndScores()
    {
        var modelDirectory = FindModelDirectory();
        using var model = new OnnxYoloV9RawModel(
            Path.Combine(modelDirectory, "yolo-v9-s-608-license-plates-raw.onnx"));

        var output = model.Run(new float[YoloV9RawPlateDetector.InputValueCount]);

        Assert.Equal(7_581 * 4, output.Boxes.Length);
        Assert.Equal(7_581, output.Scores.Length);
        Assert.Equal(YoloV9InputLayout.ChannelsFirst, model.InputLayout);
        Assert.StartsWith("ONNX Runtime", model.BackendName, StringComparison.Ordinal);
    }

    private static string FindModelDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("DEVEMOBILELPR_MODEL_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "artifacts", "models");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Model directory was not found. Run eng/Download-Models.ps1 first.");
    }

    private static Yuv420Frame CreateFrame(int width, int height)
    {
        var yLength = width * height;
        var chromaLength = width * height / 4;
        var y = MemoryPool<byte>.Shared.Rent(yLength);
        var u = MemoryPool<byte>.Shared.Rent(chromaLength);
        var v = MemoryPool<byte>.Shared.Rent(chromaLength);
        y.Memory.Span[..yLength].Fill(128);
        u.Memory.Span[..chromaLength].Fill(128);
        v.Memory.Span[..chromaLength].Fill(128);
        return new Yuv420Frame(1, DateTimeOffset.UtcNow, width, height, 0,
            y, yLength, width, 1,
            u, chromaLength, width / 2, 1,
            v, chromaLength, width / 2, 1);
    }
}
