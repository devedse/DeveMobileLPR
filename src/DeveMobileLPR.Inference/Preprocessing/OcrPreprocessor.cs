using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference.Models;

namespace DeveMobileLPR.Inference.Preprocessing;

internal static class OcrPreprocessor
{
    public static void Fill(Yuv420Frame frame, BoundingBox source, Span<byte> tensor)
    {
        var required = CctV2Metadata.Width * CctV2Metadata.Height * CctV2Metadata.Channels;
        if (tensor.Length < required)
        {
            throw new ArgumentException($"OCR tensor requires {required} bytes.", nameof(tensor));
        }

        source = source.Clamp(frame.OrientedWidth, frame.OrientedHeight);
        var sampler = new YuvImageSampler(frame);
        for (var y = 0; y < CctV2Metadata.Height; y++)
        {
            var sourceY = source.Top + (y + 0.5f) * source.Height / CctV2Metadata.Height - 0.5f;
            for (var x = 0; x < CctV2Metadata.Width; x++)
            {
                var sourceX = source.Left + (x + 0.5f) * source.Width / CctV2Metadata.Width - 0.5f;
                sampler.SampleBilinear(sourceX, sourceY, out var red, out var green, out var blue);
                var offset = (y * CctV2Metadata.Width + x) * CctV2Metadata.Channels;
                tensor[offset] = red;
                tensor[offset + 1] = green;
                tensor[offset + 2] = blue;
            }
        }
    }
}
