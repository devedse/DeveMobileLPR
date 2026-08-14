using Android.Content;
using Android.Graphics;
using Android.Views;
using DeveMobileLPR.Geometry;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

/// <summary>
/// Displays a camera buffer without changing its proportions. It reapplies the transform whenever
/// Android changes the panel size, which keeps rotation and split-screen layout changes coherent.
/// </summary>
internal sealed class AspectRatioTextureView(Context context) : TextureView(context)
{
    private int _bufferWidth;
    private int _bufferHeight;
    private int _rotationDegrees;
    private AspectScaleMode _scaleMode;
    private bool _mirrorHorizontally;

    public void ConfigureBuffer(
        int bufferWidth,
        int bufferHeight,
        int rotationDegrees,
        AspectScaleMode scaleMode,
        bool mirrorHorizontally)
    {
        _bufferWidth = bufferWidth;
        _bufferHeight = bufferHeight;
        _rotationDegrees = rotationDegrees;
        _scaleMode = scaleMode;
        _mirrorHorizontally = mirrorHorizontally;
        ApplyAspectTransform();
    }

    protected override void OnSizeChanged(int width, int height, int oldWidth, int oldHeight)
    {
        base.OnSizeChanged(width, height, oldWidth, oldHeight);
        ApplyAspectTransform();
    }

    private void ApplyAspectTransform()
    {
        if (Width <= 0 || Height <= 0 || _bufferWidth <= 0 || _bufferHeight <= 0)
        {
            return;
        }

        // A Camera2 SurfaceTexture is not an ordinary bitmap: Android's camera producer
        // supplies its own buffer transform. Correct the producer's aspect first, then apply
        // the display rotation. Feeding the display rotation into the generic bitmap transform
        // swaps width/height a second time and makes an upright landscape preview tall/narrow.
        var correction = AspectRatioCorrection.Create(
            _bufferWidth,
            _bufferHeight,
            Width,
            Height,
            clockwiseRotationDegrees: 0,
            _scaleMode,
            _mirrorHorizontally);
        using var matrix = new Matrix();
        matrix.SetValues([
            correction.ScaleX, correction.SkewX, correction.TranslateX,
            correction.SkewY, correction.ScaleY, correction.TranslateY,
            0f, 0f, 1f
        ]);
        matrix.PostRotate(_rotationDegrees, Width / 2f, Height / 2f);
        SetTransform(matrix);
    }
}
