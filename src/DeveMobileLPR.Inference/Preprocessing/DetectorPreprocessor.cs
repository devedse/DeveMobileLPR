using System.Diagnostics;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference.Yolo;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Inference.Preprocessing;

internal readonly record struct LetterboxTransform(BoundingBox Source, float Scale, float PaddingX, float PaddingY)
{
    public BoundingBox ToSource(BoundingBox modelBounds, int frameWidth, int frameHeight) => new BoundingBox(
        Source.Left + (modelBounds.Left - PaddingX) / Scale,
        Source.Top + (modelBounds.Top - PaddingY) / Scale,
        Source.Left + (modelBounds.Right - PaddingX) / Scale,
        Source.Top + (modelBounds.Bottom - PaddingY) / Scale).Clamp(frameWidth, frameHeight);
}

internal readonly record struct DetectorPreprocessingResult(
    LetterboxTransform Transform,
    DetectorPreparationTiming Timing);

internal static class DetectorPreprocessor
{
    public const int InputSize = 608;
    private const float PaddingValue = 114f / 255f;

    // Exact per-byte values of value / 255f so the hot loop avoids three float divisions per pixel.
    private static readonly float[] ByteToUnit = CreateByteToUnit();

    private static float[] CreateByteToUnit()
    {
        var table = new float[256];
        for (var value = 0; value < table.Length; value++)
        {
            table[value] = value / 255f;
        }

        return table;
    }

    public static LetterboxTransform Fill(
        Yuv420Frame frame,
        BoundingBox source,
        Span<float> tensor,
        YoloV9InputLayout layout = YoloV9InputLayout.ChannelsFirst) =>
        FillMeasured(frame, source, tensor, layout).Transform;

    public static DetectorPreprocessingResult FillMeasured(
        Yuv420Frame frame,
        BoundingBox source,
        Span<float> tensor,
        YoloV9InputLayout layout = YoloV9InputLayout.ChannelsFirst)
    {
        var required = 3 * InputSize * InputSize;
        if (tensor.Length < required)
        {
            throw new ArgumentException($"Detector tensor requires {required} values.", nameof(tensor));
        }

        var stageStartedAt = Stopwatch.GetTimestamp();
        source = source.Clamp(frame.OrientedWidth, frame.OrientedHeight);
        var scale = Math.Min(InputSize / source.Width, InputSize / source.Height);
        var resizedWidth = (int)MathF.Round(source.Width * scale);
        var resizedHeight = (int)MathF.Round(source.Height * scale);
        var paddingX = (InputSize - resizedWidth) / 2f;
        var paddingY = (InputSize - resizedHeight) / 2f;
        var setupMilliseconds = Stopwatch.GetElapsedTime(stageStartedAt).TotalMilliseconds;

        stageStartedAt = Stopwatch.GetTimestamp();
        tensor[..required].Fill(PaddingValue);
        var tensorFillMilliseconds = Stopwatch.GetElapsedTime(stageStartedAt).TotalMilliseconds;

        stageStartedAt = Stopwatch.GetTimestamp();
        var planeSize = InputSize * InputSize;
        var sampler = new YuvImageSampler(frame);

        var left = (int)MathF.Round(paddingX - 0.1f);
        var top = (int)MathF.Round(paddingY - 0.1f);
        var maxX = frame.OrientedWidth - 1;
        var maxY = frame.OrientedHeight - 1;

        // Horizontal sample geometry is identical on every row; compute the visible columns once.
        Span<int> columnOutputX = stackalloc int[InputSize];
        Span<int> columnX0 = stackalloc int[InputSize];
        Span<int> columnX1 = stackalloc int[InputSize];
        Span<float> columnWeightX = stackalloc float[InputSize];
        var columnCount = 0;
        for (var targetX = 0; targetX < resizedWidth; targetX++)
        {
            var outputX = targetX + left;
            if ((uint)outputX >= InputSize)
            {
                continue;
            }

            var sourceX = source.Left + (targetX + 0.5f) / scale - 0.5f;
            var x0 = Math.Clamp((int)MathF.Floor(sourceX), 0, maxX);
            columnOutputX[columnCount] = outputX;
            columnX0[columnCount] = x0;
            columnX1[columnCount] = Math.Min(x0 + 1, maxX);
            columnWeightX[columnCount] = Math.Clamp(sourceX - x0, 0, 1);
            columnCount++;
        }

        var byteToUnit = ByteToUnit;
        for (var targetY = 0; targetY < resizedHeight; targetY++)
        {
            var outputY = targetY + top;
            if ((uint)outputY >= InputSize)
            {
                continue;
            }

            var sourceY = source.Top + (targetY + 0.5f) / scale - 0.5f;
            var y0 = Math.Clamp((int)MathF.Floor(sourceY), 0, maxY);
            var y1 = Math.Min(y0 + 1, maxY);
            var weightY = Math.Clamp(sourceY - y0, 0, 1);
            var rowOffset = outputY * InputSize;
            for (var column = 0; column < columnCount; column++)
            {
                sampler.SampleBilinear(
                    columnX0[column],
                    columnX1[column],
                    y0,
                    y1,
                    columnWeightX[column],
                    weightY,
                    out var red,
                    out var green,
                    out var blue);
                var pixelOffset = rowOffset + columnOutputX[column];
                if (layout == YoloV9InputLayout.ChannelsFirst)
                {
                    tensor[pixelOffset] = byteToUnit[red];
                    tensor[planeSize + pixelOffset] = byteToUnit[green];
                    tensor[2 * planeSize + pixelOffset] = byteToUnit[blue];
                }
                else
                {
                    var interleavedOffset = pixelOffset * 3;
                    tensor[interleavedOffset] = byteToUnit[red];
                    tensor[interleavedOffset + 1] = byteToUnit[green];
                    tensor[interleavedOffset + 2] = byteToUnit[blue];
                }
            }
        }

        var resampleMilliseconds = Stopwatch.GetElapsedTime(stageStartedAt).TotalMilliseconds;
        return new DetectorPreprocessingResult(
            new LetterboxTransform(source, scale, paddingX, paddingY),
            new DetectorPreparationTiming(
                setupMilliseconds,
                tensorFillMilliseconds,
                resampleMilliseconds));
    }
}
