using System.Buffers;

namespace DeveMobileLPR.Imaging;

public sealed class Yuv420Frame : IDisposable
{
    private IMemoryOwner<byte>? _yOwner;
    private IMemoryOwner<byte>? _uOwner;
    private IMemoryOwner<byte>? _vOwner;

    public Yuv420Frame(
        long sequence,
        DateTimeOffset capturedAt,
        int width,
        int height,
        int rotationDegrees,
        IMemoryOwner<byte> yOwner,
        int yLength,
        int yRowStride,
        int yPixelStride,
        IMemoryOwner<byte> uOwner,
        int uLength,
        int uRowStride,
        int uPixelStride,
        IMemoryOwner<byte> vOwner,
        int vLength,
        int vRowStride,
        int vPixelStride)
    {
        if (rotationDegrees is not (0 or 90 or 180 or 270))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationDegrees));
        }

        Sequence = sequence;
        CapturedAt = capturedAt;
        Width = width;
        Height = height;
        RotationDegrees = rotationDegrees;
        _yOwner = yOwner;
        _uOwner = uOwner;
        _vOwner = vOwner;
        YLength = yLength;
        ULength = uLength;
        VLength = vLength;
        YRowStride = yRowStride;
        YPixelStride = yPixelStride;
        URowStride = uRowStride;
        UPixelStride = uPixelStride;
        VRowStride = vRowStride;
        VPixelStride = vPixelStride;
    }

    public long Sequence { get; }
    public DateTimeOffset CapturedAt { get; }
    public int Width { get; }
    public int Height { get; }
    public int RotationDegrees { get; }
    public int OrientedWidth => RotationDegrees is 90 or 270 ? Height : Width;
    public int OrientedHeight => RotationDegrees is 90 or 270 ? Width : Height;
    public int YLength { get; }
    public int ULength { get; }
    public int VLength { get; }
    public int YRowStride { get; }
    public int YPixelStride { get; }
    public int URowStride { get; }
    public int UPixelStride { get; }
    public int VRowStride { get; }
    public int VPixelStride { get; }

    public Memory<byte> YPlane => (_yOwner ?? throw Disposed()).Memory[..YLength];
    public Memory<byte> UPlane => (_uOwner ?? throw Disposed()).Memory[..ULength];
    public Memory<byte> VPlane => (_vOwner ?? throw Disposed()).Memory[..VLength];

    public void GetRgb(int orientedX, int orientedY, out byte red, out byte green, out byte blue)
    {
        if ((uint)orientedX >= (uint)OrientedWidth || (uint)orientedY >= (uint)OrientedHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(orientedX));
        }

        var (rawX, rawY) = ToRawCoordinates(orientedX, orientedY);
        var yIndex = rawY * YRowStride + rawX * YPixelStride;
        var chromaX = rawX / 2;
        var chromaY = rawY / 2;
        var uIndex = chromaY * URowStride + chromaX * UPixelStride;
        var vIndex = chromaY * VRowStride + chromaX * VPixelStride;

        var yPlane = YPlane.Span;
        var uPlane = UPlane.Span;
        var vPlane = VPlane.Span;
        var y = yIndex < yPlane.Length ? yPlane[yIndex] : (byte)16;
        var u = uIndex < uPlane.Length ? uPlane[uIndex] : (byte)128;
        var v = vIndex < vPlane.Length ? vPlane[vIndex] : (byte)128;
        ConvertYuvToRgb(y, u, v, out red, out green, out blue);
    }

    public static void ConvertYuvToRgb(byte y, byte u, byte v, out byte red, out byte green, out byte blue)
    {
        var c = Math.Max(0, y - 16);
        var d = u - 128;
        var e = v - 128;
        red = ClampToByte((298 * c + 409 * e + 128) >> 8);
        green = ClampToByte((298 * c - 100 * d - 208 * e + 128) >> 8);
        blue = ClampToByte((298 * c + 516 * d + 128) >> 8);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _yOwner, null)?.Dispose();
        Interlocked.Exchange(ref _uOwner, null)?.Dispose();
        Interlocked.Exchange(ref _vOwner, null)?.Dispose();
    }

    private (int X, int Y) ToRawCoordinates(int x, int y) => RotationDegrees switch
    {
        0 => (x, y),
        90 => (y, Height - 1 - x),
        180 => (Width - 1 - x, Height - 1 - y),
        270 => (Width - 1 - y, x),
        _ => throw new InvalidOperationException()
    };

    private static byte ClampToByte(int value) => (byte)Math.Clamp(value, 0, 255);
    private static ObjectDisposedException Disposed() => new(nameof(Yuv420Frame));
}
