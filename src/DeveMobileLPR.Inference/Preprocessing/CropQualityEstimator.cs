using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Inference.Preprocessing;

internal static class CropQualityEstimator
{
    public static float Estimate(Yuv420Frame frame, BoundingBox bounds)
    {
        bounds = bounds.Clamp(frame.OrientedWidth, frame.OrientedHeight);
        if (bounds.Width < 8 || bounds.Height < 4)
        {
            return 0;
        }

        const int columns = 24;
        const int rows = 10;
        var luminance = new float[columns * rows];
        var mean = 0f;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                var sourceX = bounds.Left + (x + 0.5f) * bounds.Width / columns;
                var sourceY = bounds.Top + (y + 0.5f) * bounds.Height / rows;
                YuvImageSampler.SampleBilinear(frame, sourceX, sourceY, out var red, out var green, out var blue);
                var value = 0.2126f * red + 0.7152f * green + 0.0722f * blue;
                luminance[y * columns + x] = value;
                mean += value;
            }
        }

        mean /= luminance.Length;
        var edge = 0f;
        for (var y = 1; y < rows - 1; y++)
        {
            for (var x = 1; x < columns - 1; x++)
            {
                var horizontal = luminance[y * columns + x + 1] - luminance[y * columns + x - 1];
                var vertical = luminance[(y + 1) * columns + x] - luminance[(y - 1) * columns + x];
                edge += MathF.Sqrt(horizontal * horizontal + vertical * vertical);
            }
        }

        var sharpness = Math.Clamp(edge / ((columns - 2) * (rows - 2) * 55f), 0, 1);
        var exposure = 1 - Math.Clamp(Math.Abs(mean - 130f) / 130f, 0, 1);
        var size = Math.Clamp(bounds.Width / 140f, 0, 1);
        return Math.Clamp(0.55f * sharpness + 0.25f * exposure + 0.20f * size, 0.05f, 1);
    }
}
