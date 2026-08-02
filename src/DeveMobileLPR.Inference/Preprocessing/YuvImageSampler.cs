using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.Inference.Preprocessing;

/// <summary>
/// A per-frame sampler that pins the plane spans and layout metadata for the duration of
/// preprocessing. Keeping these values locally avoids resolving three pooled memories and
/// their spans for every one of the millions of source samples used by a detector frame.
/// </summary>
internal readonly ref struct YuvImageSampler
{
    private readonly ReadOnlySpan<byte> _yPlane;
    private readonly ReadOnlySpan<byte> _uPlane;
    private readonly ReadOnlySpan<byte> _vPlane;
    private readonly int _width;
    private readonly int _height;
    private readonly int _orientedWidth;
    private readonly int _orientedHeight;
    private readonly int _rotationDegrees;
    private readonly int _yRowStride;
    private readonly int _yPixelStride;
    private readonly int _uRowStride;
    private readonly int _uPixelStride;
    private readonly int _vRowStride;
    private readonly int _vPixelStride;

    public YuvImageSampler(Yuv420Frame frame)
    {
        _yPlane = frame.YPlane.Span;
        _uPlane = frame.UPlane.Span;
        _vPlane = frame.VPlane.Span;
        _width = frame.Width;
        _height = frame.Height;
        _orientedWidth = frame.OrientedWidth;
        _orientedHeight = frame.OrientedHeight;
        _rotationDegrees = frame.RotationDegrees;
        _yRowStride = frame.YRowStride;
        _yPixelStride = frame.YPixelStride;
        _uRowStride = frame.URowStride;
        _uPixelStride = frame.UPixelStride;
        _vRowStride = frame.VRowStride;
        _vPixelStride = frame.VPixelStride;
    }

    public void SampleBilinear(
        float x,
        float y,
        out byte red,
        out byte green,
        out byte blue)
    {
        var x0 = Math.Clamp((int)MathF.Floor(x), 0, _orientedWidth - 1);
        var y0 = Math.Clamp((int)MathF.Floor(y), 0, _orientedHeight - 1);
        var x1 = Math.Min(x0 + 1, _orientedWidth - 1);
        var y1 = Math.Min(y0 + 1, _orientedHeight - 1);
        var wx = Math.Clamp(x - x0, 0, 1);
        var wy = Math.Clamp(y - y0, 0, 1);

        GetYuv(x0, y0, out var y00, out var u00, out var v00);
        GetYuv(x1, y0, out var y10, out var u10, out var v10);
        GetYuv(x0, y1, out var y01, out var u01, out var v01);
        GetYuv(x1, y1, out var y11, out var u11, out var v11);

        var interpolatedY = Interpolate(y00, y10, y01, y11, wx, wy);
        var interpolatedU = Interpolate(u00, u10, u01, u11, wx, wy);
        var interpolatedV = Interpolate(v00, v10, v01, v11, wx, wy);
        Yuv420Frame.ConvertYuvToRgb(interpolatedY, interpolatedU, interpolatedV, out red, out green, out blue);
    }

    private void GetYuv(int orientedX, int orientedY, out byte y, out byte u, out byte v)
    {
        var (rawX, rawY) = _rotationDegrees switch
        {
            0 => (orientedX, orientedY),
            90 => (orientedY, _height - 1 - orientedX),
            180 => (_width - 1 - orientedX, _height - 1 - orientedY),
            270 => (_width - 1 - orientedY, orientedX),
            _ => throw new InvalidOperationException()
        };
        var chromaX = rawX / 2;
        var chromaY = rawY / 2;
        var yIndex = rawY * _yRowStride + rawX * _yPixelStride;
        var uIndex = chromaY * _uRowStride + chromaX * _uPixelStride;
        var vIndex = chromaY * _vRowStride + chromaX * _vPixelStride;
        y = yIndex < _yPlane.Length ? _yPlane[yIndex] : (byte)16;
        u = uIndex < _uPlane.Length ? _uPlane[uIndex] : (byte)128;
        v = vIndex < _vPlane.Length ? _vPlane[vIndex] : (byte)128;
    }

    private static byte Interpolate(byte topLeft, byte topRight, byte bottomLeft, byte bottomRight, float x, float y)
    {
        var top = topLeft + (topRight - topLeft) * x;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * x;
        return (byte)Math.Clamp((int)MathF.Round(top + (bottom - top) * y), 0, 255);
    }
}
