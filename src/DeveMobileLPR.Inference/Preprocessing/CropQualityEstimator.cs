using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Inference.Preprocessing;

internal static class CropQualityEstimator
{
    public static float Estimate(
        Yuv420Frame frame,
        BoundingBox bounds,
        RecognitionTuningConfiguration configuration)
    {
        bounds = bounds.Clamp(frame.OrientedWidth, frame.OrientedHeight);
        if (bounds.Width < configuration.CropQuality_MinimumCropWidthPixels
            || bounds.Height < configuration.CropQuality_MinimumCropHeightPixels
            || TouchesFrameEdge(
                bounds,
                frame.OrientedWidth,
                frame.OrientedHeight,
                configuration.CropQuality_FrameEdgeMarginPixels))
        {
            return 0;
        }

        var columns = configuration.CropQuality_SampleColumns;
        var rows = configuration.CropQuality_SampleRows;
        var luminance = new float[columns * rows];
        var mean = 0f;
        var sampler = new YuvImageSampler(frame);
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                var sourceX = bounds.Left + (x + 0.5f) * bounds.Width / columns;
                var sourceY = bounds.Top + (y + 0.5f) * bounds.Height / rows;
                sampler.SampleBilinear(sourceX, sourceY, out var red, out var green, out var blue);
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

        var sharpness = Math.Clamp(
            edge / ((columns - 2) * (rows - 2) * configuration.CropQuality_SharpnessNormalization),
            0,
            1);
        var exposure = 1 - Math.Clamp(
            Math.Abs(mean - configuration.CropQuality_TargetLuminance) / configuration.CropQuality_ExposureRange,
            0,
            1);
        var size = Math.Clamp(bounds.Width / configuration.CropQuality_FullSizeWidthPixels, 0, 1);
        return Math.Clamp(
            configuration.CropQuality_SharpnessWeight * sharpness
            + configuration.CropQuality_ExposureWeight * exposure
            + configuration.CropQuality_SizeWeight * size,
            configuration.CropQuality_MinimumScore,
            1);
    }

    private static bool TouchesFrameEdge(
        BoundingBox bounds,
        int frameWidth,
        int frameHeight,
        float margin) =>
        bounds.Left <= margin
        || bounds.Top <= margin
        || bounds.Right >= frameWidth - margin
        || bounds.Bottom >= frameHeight - margin;
}
