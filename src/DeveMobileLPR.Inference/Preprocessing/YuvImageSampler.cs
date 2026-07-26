using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Inference.Preprocessing;

internal static class YuvImageSampler
{
    public static void SampleBilinear(
        Yuv420Frame frame,
        float x,
        float y,
        out byte red,
        out byte green,
        out byte blue)
    {
        var x0 = Math.Clamp((int)MathF.Floor(x), 0, frame.OrientedWidth - 1);
        var y0 = Math.Clamp((int)MathF.Floor(y), 0, frame.OrientedHeight - 1);
        var x1 = Math.Min(x0 + 1, frame.OrientedWidth - 1);
        var y1 = Math.Min(y0 + 1, frame.OrientedHeight - 1);
        var wx = Math.Clamp(x - x0, 0, 1);
        var wy = Math.Clamp(y - y0, 0, 1);

        frame.GetRgb(x0, y0, out var r00, out var g00, out var b00);
        frame.GetRgb(x1, y0, out var r10, out var g10, out var b10);
        frame.GetRgb(x0, y1, out var r01, out var g01, out var b01);
        frame.GetRgb(x1, y1, out var r11, out var g11, out var b11);

        red = Interpolate(r00, r10, r01, r11, wx, wy);
        green = Interpolate(g00, g10, g01, g11, wx, wy);
        blue = Interpolate(b00, b10, b01, b11, wx, wy);
    }

    private static byte Interpolate(byte topLeft, byte topRight, byte bottomLeft, byte bottomRight, float x, float y)
    {
        var top = topLeft + (topRight - topLeft) * x;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * x;
        return (byte)Math.Clamp((int)MathF.Round(top + (bottom - top) * y), 0, 255);
    }
}
