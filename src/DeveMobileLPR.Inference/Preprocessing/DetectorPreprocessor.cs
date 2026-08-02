using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
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

    private static readonly Vector128<int> Bias16 = Vector128.Create(16);
    private static readonly Vector128<int> Bias128 = Vector128.Create(128);
    private static readonly Vector128<int> Coefficient298 = Vector128.Create(298);
    private static readonly Vector128<int> Coefficient409 = Vector128.Create(409);
    private static readonly Vector128<int> Coefficient100 = Vector128.Create(100);
    private static readonly Vector128<int> Coefficient208 = Vector128.Create(208);
    private static readonly Vector128<int> Coefficient516 = Vector128.Create(516);
    private static readonly Vector128<int> Byte255 = Vector128.Create(255);
    private static readonly Vector128<float> Float255 = Vector128.Create(255f);

    // The vectorized quad path is bit-identical to the scalar sampler path; tests toggle this to prove it.
    internal static bool UseVectorizedResampler = Vector128.IsHardwareAccelerated;

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

        // Horizontal sample geometry is identical on every row; compute the visible columns once,
        // including separable plane-index terms so corners resolve with one addition per plane.
        Span<int> columnOutputX = stackalloc int[InputSize];
        Span<int> columnX0 = stackalloc int[InputSize];
        Span<int> columnX1 = stackalloc int[InputSize];
        Span<float> columnWeightX = stackalloc float[InputSize];
        Span<int> columnYTerm0 = stackalloc int[InputSize];
        Span<int> columnYTerm1 = stackalloc int[InputSize];
        Span<int> columnUTerm0 = stackalloc int[InputSize];
        Span<int> columnUTerm1 = stackalloc int[InputSize];
        Span<int> columnVTerm0 = stackalloc int[InputSize];
        Span<int> columnVTerm1 = stackalloc int[InputSize];
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
            var x1 = Math.Min(x0 + 1, maxX);
            columnOutputX[columnCount] = outputX;
            columnX0[columnCount] = x0;
            columnX1[columnCount] = x1;
            columnWeightX[columnCount] = Math.Clamp(sourceX - x0, 0, 1);
            ComputeColumnIndexTerms(frame, x0, out var yTerm0, out var uTerm0, out var vTerm0);
            ComputeColumnIndexTerms(frame, x1, out var yTerm1, out var uTerm1, out var vTerm1);
            columnYTerm0[columnCount] = yTerm0;
            columnYTerm1[columnCount] = yTerm1;
            columnUTerm0[columnCount] = uTerm0;
            columnUTerm1[columnCount] = uTerm1;
            columnVTerm0[columnCount] = vTerm0;
            columnVTerm1[columnCount] = vTerm1;
            columnCount++;
        }

        var byteToUnit = ByteToUnit;
        var useVectorizedPath = UseVectorizedResampler;
        var yPlane = frame.YPlane.Span;
        var uPlane = frame.UPlane.Span;
        var vPlane = frame.VPlane.Span;
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
            var column = 0;
            if (useVectorizedPath)
            {
                ComputeRowIndexTerms(frame, y0, out var yRow0, out var uRow0, out var vRow0);
                ComputeRowIndexTerms(frame, y1, out var yRow1, out var uRow1, out var vRow1);
                var weightYVector = Vector128.Create(weightY);
                for (; column + 4 <= columnCount; column += 4)
                {
                    ConvertYuvQuad(
                        GatherQuad(yPlane, columnYTerm0, column, yRow0, 16),
                        GatherQuad(uPlane, columnUTerm0, column, uRow0, 128),
                        GatherQuad(vPlane, columnVTerm0, column, vRow0, 128),
                        out var redTopLeft, out var greenTopLeft, out var blueTopLeft);
                    ConvertYuvQuad(
                        GatherQuad(yPlane, columnYTerm1, column, yRow0, 16),
                        GatherQuad(uPlane, columnUTerm1, column, uRow0, 128),
                        GatherQuad(vPlane, columnVTerm1, column, vRow0, 128),
                        out var redTopRight, out var greenTopRight, out var blueTopRight);
                    ConvertYuvQuad(
                        GatherQuad(yPlane, columnYTerm0, column, yRow1, 16),
                        GatherQuad(uPlane, columnUTerm0, column, uRow1, 128),
                        GatherQuad(vPlane, columnVTerm0, column, vRow1, 128),
                        out var redBottomLeft, out var greenBottomLeft, out var blueBottomLeft);
                    ConvertYuvQuad(
                        GatherQuad(yPlane, columnYTerm1, column, yRow1, 16),
                        GatherQuad(uPlane, columnUTerm1, column, uRow1, 128),
                        GatherQuad(vPlane, columnVTerm1, column, vRow1, 128),
                        out var redBottomRight, out var greenBottomRight, out var blueBottomRight);

                    var weightXVector = Vector128.Create<float>(columnWeightX.Slice(column, 4));
                    var red = InterpolateQuad(redTopLeft, redTopRight, redBottomLeft, redBottomRight, weightXVector, weightYVector);
                    var green = InterpolateQuad(greenTopLeft, greenTopRight, greenBottomLeft, greenBottomRight, weightXVector, weightYVector);
                    var blue = InterpolateQuad(blueTopLeft, blueTopRight, blueBottomLeft, blueBottomRight, weightXVector, weightYVector);

                    // Visible output columns are consecutive by construction, so quad stores are contiguous.
                    var pixelOffset = rowOffset + columnOutputX[column];
                    if (layout == YoloV9InputLayout.ChannelsFirst)
                    {
                        red.CopyTo(tensor.Slice(pixelOffset, 4));
                        green.CopyTo(tensor.Slice(planeSize + pixelOffset, 4));
                        blue.CopyTo(tensor.Slice(2 * planeSize + pixelOffset, 4));
                    }
                    else
                    {
                        for (var lane = 0; lane < 4; lane++)
                        {
                            var interleavedOffset = (pixelOffset + lane) * 3;
                            tensor[interleavedOffset] = red.GetElement(lane);
                            tensor[interleavedOffset + 1] = green.GetElement(lane);
                            tensor[interleavedOffset + 2] = blue.GetElement(lane);
                        }
                    }
                }
            }

            for (; column < columnCount; column++)
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

    // The rotated plane indices are separable: each corner index is columnTerm(x) + rowTerm(y),
    // so the rotation switch, stride multiplies, and chroma divisions run once per column/row.
    private static void ComputeColumnIndexTerms(Yuv420Frame frame, int orientedX, out int yTerm, out int uTerm, out int vTerm)
    {
        switch (frame.RotationDegrees)
        {
            case 0:
                yTerm = orientedX * frame.YPixelStride;
                uTerm = orientedX / 2 * frame.UPixelStride;
                vTerm = orientedX / 2 * frame.VPixelStride;
                break;
            case 90:
            {
                var rawY = frame.Height - 1 - orientedX;
                yTerm = rawY * frame.YRowStride;
                uTerm = rawY / 2 * frame.URowStride;
                vTerm = rawY / 2 * frame.VRowStride;
                break;
            }
            case 180:
            {
                var rawX = frame.Width - 1 - orientedX;
                yTerm = rawX * frame.YPixelStride;
                uTerm = rawX / 2 * frame.UPixelStride;
                vTerm = rawX / 2 * frame.VPixelStride;
                break;
            }
            default:
                yTerm = orientedX * frame.YRowStride;
                uTerm = orientedX / 2 * frame.URowStride;
                vTerm = orientedX / 2 * frame.VRowStride;
                break;
        }
    }

    private static void ComputeRowIndexTerms(Yuv420Frame frame, int orientedY, out int yTerm, out int uTerm, out int vTerm)
    {
        switch (frame.RotationDegrees)
        {
            case 0:
                yTerm = orientedY * frame.YRowStride;
                uTerm = orientedY / 2 * frame.URowStride;
                vTerm = orientedY / 2 * frame.VRowStride;
                break;
            case 90:
                yTerm = orientedY * frame.YPixelStride;
                uTerm = orientedY / 2 * frame.UPixelStride;
                vTerm = orientedY / 2 * frame.VPixelStride;
                break;
            case 180:
            {
                var rawY = frame.Height - 1 - orientedY;
                yTerm = rawY * frame.YRowStride;
                uTerm = rawY / 2 * frame.URowStride;
                vTerm = rawY / 2 * frame.VRowStride;
                break;
            }
            default:
            {
                var rawX = frame.Width - 1 - orientedY;
                yTerm = rawX * frame.YPixelStride;
                uTerm = rawX / 2 * frame.UPixelStride;
                vTerm = rawX / 2 * frame.VPixelStride;
                break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LoadOrFallback(ReadOnlySpan<byte> plane, int index, int fallback) =>
        (uint)index < (uint)plane.Length ? plane[index] : fallback;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> GatherQuad(
        ReadOnlySpan<byte> plane,
        ReadOnlySpan<int> columnTerms,
        int column,
        int rowTerm,
        int fallback) =>
        Vector128.Create(
            LoadOrFallback(plane, rowTerm + columnTerms[column], fallback),
            LoadOrFallback(plane, rowTerm + columnTerms[column + 1], fallback),
            LoadOrFallback(plane, rowTerm + columnTerms[column + 2], fallback),
            LoadOrFallback(plane, rowTerm + columnTerms[column + 3], fallback));

    // Vector form of Yuv420Frame.ConvertYuvToRgb for four samples; identical integer math.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertYuvQuad(
        Vector128<int> y,
        Vector128<int> u,
        Vector128<int> v,
        out Vector128<float> red,
        out Vector128<float> green,
        out Vector128<float> blue)
    {
        var luma = Vector128.Max(y - Bias16, Vector128<int>.Zero) * Coefficient298 + Bias128;
        var d = u - Bias128;
        var e = v - Bias128;
        red = Vector128.ConvertToSingle(ClampToByteRange(Vector128.ShiftRightArithmetic(luma + e * Coefficient409, 8)));
        green = Vector128.ConvertToSingle(ClampToByteRange(Vector128.ShiftRightArithmetic(luma - d * Coefficient100 - e * Coefficient208, 8)));
        blue = Vector128.ConvertToSingle(ClampToByteRange(Vector128.ShiftRightArithmetic(luma + d * Coefficient516, 8)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ClampToByteRange(Vector128<int> value) =>
        Vector128.Min(Vector128.Max(value, Vector128<int>.Zero), Byte255);

    // Vector form of the sampler's RGB-space bilinear interpolation plus the /255 normalization;
    // the operation order matches the scalar path exactly so results stay bit-identical.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> InterpolateQuad(
        Vector128<float> topLeft,
        Vector128<float> topRight,
        Vector128<float> bottomLeft,
        Vector128<float> bottomRight,
        Vector128<float> weightX,
        Vector128<float> weightY)
    {
        var top = topLeft + (topRight - topLeft) * weightX;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * weightX;
        var value = Vector128.Round(top + (bottom - top) * weightY);
        return Vector128.Min(Vector128.Max(value, Vector128<float>.Zero), Float255) / Float255;
    }
}
