using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Inference.Preprocessing;

internal readonly record struct LetterboxTransform(BoundingBox Source, float Scale, float PaddingX, float PaddingY)
{
    public BoundingBox ToSource(BoundingBox modelBounds, int frameWidth, int frameHeight) => new BoundingBox(
        Source.Left + (modelBounds.Left - PaddingX) / Scale,
        Source.Top + (modelBounds.Top - PaddingY) / Scale,
        Source.Left + (modelBounds.Right - PaddingX) / Scale,
        Source.Top + (modelBounds.Bottom - PaddingY) / Scale).Clamp(frameWidth, frameHeight);
}

internal static class DetectorPreprocessor
{
    public const int InputSize = 608;
    private const float PaddingValue = 114f / 255f;

    public static LetterboxTransform Fill(
        Yuv420Frame frame,
        BoundingBox source,
        Span<float> tensor)
    {
        var required = 3 * InputSize * InputSize;
        if (tensor.Length < required)
        {
            throw new ArgumentException($"Detector tensor requires {required} values.", nameof(tensor));
        }

        source = source.Clamp(frame.OrientedWidth, frame.OrientedHeight);
        var scale = Math.Min(InputSize / source.Width, InputSize / source.Height);
        var resizedWidth = (int)MathF.Round(source.Width * scale);
        var resizedHeight = (int)MathF.Round(source.Height * scale);
        var paddingX = (InputSize - resizedWidth) / 2f;
        var paddingY = (InputSize - resizedHeight) / 2f;
        tensor[..required].Fill(PaddingValue);
        var planeSize = InputSize * InputSize;

        var left = (int)MathF.Round(paddingX - 0.1f);
        var top = (int)MathF.Round(paddingY - 0.1f);
        for (var targetY = 0; targetY < resizedHeight; targetY++)
        {
            var sourceY = source.Top + (targetY + 0.5f) / scale - 0.5f;
            var outputY = targetY + top;
            if ((uint)outputY >= InputSize)
            {
                continue;
            }

            for (var targetX = 0; targetX < resizedWidth; targetX++)
            {
                var sourceX = source.Left + (targetX + 0.5f) / scale - 0.5f;
                var outputX = targetX + left;
                if ((uint)outputX >= InputSize)
                {
                    continue;
                }

                YuvImageSampler.SampleBilinear(frame, sourceX, sourceY, out var red, out var green, out var blue);
                var offset = outputY * InputSize + outputX;
                tensor[offset] = red / 255f;
                tensor[planeSize + offset] = green / 255f;
                tensor[2 * planeSize + offset] = blue / 255f;
            }
        }

        return new LetterboxTransform(source, scale, paddingX, paddingY);
    }
}
