using System.Runtime.InteropServices;
using DeveMobileLPR.Imaging;
using Windows.Graphics.Imaging;

namespace DeveMobileLPR.App.Platforms.Windows.Camera;

internal static class WindowsSoftwareBitmapConverter
{
    public static unsafe Yuv420Frame ToYuv420Frame(
        SoftwareBitmap bitmap,
        long sequence,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            throw new ArgumentException("The software bitmap must use BGRA8 pixels.", nameof(bitmap));
        }

        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        ((IMemoryBufferByteAccess)reference).GetBuffer(out var data, out var capacity);
        var plane = buffer.GetPlaneDescription(0);
        var requiredLength = checked(plane.Stride * bitmap.PixelHeight);
        if (plane.StartIndex < 0
            || requiredLength < 0
            || checked((uint)(plane.StartIndex + requiredLength)) > capacity)
        {
            throw new InvalidDataException("The software bitmap exposes an invalid BGRA buffer layout.");
        }

        var pixels = new ReadOnlySpan<byte>(data + plane.StartIndex, requiredLength);
        return BgraFrameFactory.Create(
            pixels,
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            plane.Stride,
            sequence,
            timestamp);
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-8656-1D76863FA917")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* value, out uint capacity);
    }
}
