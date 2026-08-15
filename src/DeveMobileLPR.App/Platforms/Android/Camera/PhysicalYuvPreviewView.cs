using System.Buffers;
using Android.Content;
using Android.Graphics;
using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

/// <summary>
/// Renders a throttled, downsampled preview from the same YUV frame sent to recognition.
/// This deliberately is not a Camera2 output surface: a physical-camera pair therefore
/// consumes two YUV outputs in total instead of two preview plus two YUV outputs.
/// </summary>
internal sealed class PhysicalYuvPreviewView : global::Android.Views.View
{
    private const int MaximumPreviewWidth = 640;
    private const int MaximumPreviewHeight = 480;
    private readonly object _bitmapGate = new();
    private readonly global::Android.Graphics.Paint _paint = new() { FilterBitmap = true, Dither = true };
    private Bitmap? _displayed;
    private Bitmap? _spare;
    private int _presentationPending;
    private bool _disposed;

    public PhysicalYuvPreviewView(Context context) : base(context)
    {
        SetBackgroundColor(global::Android.Graphics.Color.Black);
        SetWillNotDraw(false);
    }

    public event Action? FramePresented;

    public int RenderWidth { get; private set; }
    public int RenderHeight { get; private set; }
    public bool CanAcceptFrame => !_disposed && Volatile.Read(ref _presentationPending) == 0;

    public bool TryPresent(
        int sourceWidth,
        int sourceHeight,
        int rotationDegrees,
        bool mirrorHorizontally,
        byte[] yPlane,
        int yLength,
        int yRowStride,
        int yPixelStride,
        byte[] uPlane,
        int uLength,
        int uRowStride,
        int uPixelStride,
        byte[] vPlane,
        int vLength,
        int vRowStride,
        int vPixelStride)
    {
        if (_disposed || Interlocked.CompareExchange(ref _presentationPending, 1, 0) != 0)
        {
            return false;
        }

        Bitmap? target = null;
        int[]? pixels = null;
        try
        {
            var quarterTurn = rotationDegrees is 90 or 270;
            var orientedWidth = quarterTurn ? sourceHeight : sourceWidth;
            var orientedHeight = quarterTurn ? sourceWidth : sourceHeight;
            var scale = Math.Min(
                1d,
                Math.Min(
                    MaximumPreviewWidth / (double)orientedWidth,
                    MaximumPreviewHeight / (double)orientedHeight));
            var renderWidth = Math.Max(1, (int)Math.Round(orientedWidth * scale));
            var renderHeight = Math.Max(1, (int)Math.Round(orientedHeight * scale));
            RenderWidth = renderWidth;
            RenderHeight = renderHeight;

            target = TakeRenderTarget(renderWidth, renderHeight);
            pixels = ArrayPool<int>.Shared.Rent(renderWidth * renderHeight);
            FillPixels(
                pixels,
                renderWidth,
                renderHeight,
                sourceWidth,
                sourceHeight,
                rotationDegrees,
                mirrorHorizontally,
                yPlane,
                yLength,
                yRowStride,
                yPixelStride,
                uPlane,
                uLength,
                uRowStride,
                uPixelStride,
                vPlane,
                vLength,
                vRowStride,
                vPixelStride);
            target.SetPixels(pixels, 0, renderWidth, 0, 0, renderWidth, renderHeight);
            var ready = target;
            target = null;
            if (!Post(new Java.Lang.Runnable(() => PresentOnUiThread(ready))))
            {
                ready.Dispose();
                Interlocked.Exchange(ref _presentationPending, 0);
                return false;
            }
            return true;
        }
        catch
        {
            target?.Dispose();
            Interlocked.Exchange(ref _presentationPending, 0);
            throw;
        }
        finally
        {
            if (pixels is not null)
            {
                ArrayPool<int>.Shared.Return(pixels);
            }
        }
    }

    private Bitmap TakeRenderTarget(int width, int height)
    {
        lock (_bitmapGate)
        {
            if (_spare is { } spare && spare.Width == width && spare.Height == height)
            {
                _spare = null;
                return spare;
            }
            _spare?.Dispose();
            _spare = null;
        }

        return Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)
            ?? throw new InvalidOperationException("Could not allocate the physical-camera preview bitmap.");
    }

    private void PresentOnUiThread(Bitmap bitmap)
    {
        if (_disposed)
        {
            bitmap.Dispose();
            Interlocked.Exchange(ref _presentationPending, 0);
            return;
        }

        Bitmap? old;
        lock (_bitmapGate)
        {
            old = _displayed;
            _displayed = bitmap;
            _spare = old;
        }
        Invalidate();
        Interlocked.Exchange(ref _presentationPending, 0);
        FramePresented?.Invoke();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        Bitmap? bitmap;
        lock (_bitmapGate)
        {
            bitmap = _displayed;
        }
        if (bitmap is null || bitmap.IsRecycled || Width <= 0 || Height <= 0)
        {
            return;
        }

        // Draw explicitly instead of relying on ImageView drawable measurement. The view always
        // occupies its complete camera panel; one uniform scale then centers the full image inside
        // that panel. This is the native equivalent of MAUI AspectFit and can never stretch it.
        var scale = Math.Min(Width / (float)bitmap.Width, Height / (float)bitmap.Height);
        var left = (Width - bitmap.Width * scale) / 2f;
        var top = (Height - bitmap.Height * scale) / 2f;
        var saveCount = canvas.Save();
        canvas.Translate(left, top);
        canvas.Scale(scale, scale);
        canvas.DrawBitmap(bitmap, 0f, 0f, _paint);
        canvas.RestoreToCount(saveCount);
    }

    private static void FillPixels(
        int[] destination,
        int destinationWidth,
        int destinationHeight,
        int sourceWidth,
        int sourceHeight,
        int rotationDegrees,
        bool mirrorHorizontally,
        byte[] yPlane,
        int yLength,
        int yRowStride,
        int yPixelStride,
        byte[] uPlane,
        int uLength,
        int uRowStride,
        int uPixelStride,
        byte[] vPlane,
        int vLength,
        int vRowStride,
        int vPixelStride)
    {
        var quarterTurn = rotationDegrees is 90 or 270;
        var orientedWidth = quarterTurn ? sourceHeight : sourceWidth;
        var orientedHeight = quarterTurn ? sourceWidth : sourceHeight;
        for (var destinationY = 0; destinationY < destinationHeight; destinationY++)
        {
            var orientedY = Math.Min(
                orientedHeight - 1,
                (int)(((destinationY + 0.5d) * orientedHeight) / destinationHeight));
            var row = destinationY * destinationWidth;
            for (var destinationX = 0; destinationX < destinationWidth; destinationX++)
            {
                var orientedX = Math.Min(
                    orientedWidth - 1,
                    (int)(((destinationX + 0.5d) * orientedWidth) / destinationWidth));
                if (mirrorHorizontally)
                {
                    orientedX = orientedWidth - 1 - orientedX;
                }

                var (rawX, rawY) = rotationDegrees switch
                {
                    0 => (orientedX, orientedY),
                    90 => (orientedY, sourceHeight - 1 - orientedX),
                    180 => (sourceWidth - 1 - orientedX, sourceHeight - 1 - orientedY),
                    270 => (sourceWidth - 1 - orientedY, orientedX),
                    _ => throw new ArgumentOutOfRangeException(nameof(rotationDegrees))
                };
                var yIndex = rawY * yRowStride + rawX * yPixelStride;
                var chromaX = rawX / 2;
                var chromaY = rawY / 2;
                var uIndex = chromaY * uRowStride + chromaX * uPixelStride;
                var vIndex = chromaY * vRowStride + chromaX * vPixelStride;
                var y = yIndex < yLength ? yPlane[yIndex] : (byte)16;
                var u = uIndex < uLength ? uPlane[uIndex] : (byte)128;
                var v = vIndex < vLength ? vPlane[vIndex] : (byte)128;
                Yuv420Frame.ConvertYuvToRgb(y, u, v, out var red, out var green, out var blue);
                destination[row + destinationX] = unchecked((int)0xff000000)
                    | red << 16
                    | green << 8
                    | blue;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            lock (_bitmapGate)
            {
                _displayed?.Dispose();
                _displayed = null;
                _spare?.Dispose();
                _spare = null;
            }
            _paint.Dispose();
        }
        base.Dispose(disposing);
    }
}
