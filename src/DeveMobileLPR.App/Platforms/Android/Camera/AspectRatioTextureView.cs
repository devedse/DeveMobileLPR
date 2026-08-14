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

    public void ConfigureBuffer(
        int bufferWidth,
        int bufferHeight,
        int rotationDegrees,
        AspectScaleMode scaleMode)
    {
        _bufferWidth = bufferWidth;
        _bufferHeight = bufferHeight;
        _rotationDegrees = rotationDegrees;
        _scaleMode = scaleMode;
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

        var correction = AspectRatioCorrection.Create(
            _bufferWidth,
            _bufferHeight,
            Width,
            Height,
            _rotationDegrees,
            _scaleMode);
        using var matrix = new Matrix();
        matrix.SetValues([
            correction.ScaleX, correction.SkewX, correction.TranslateX,
            correction.SkewY, correction.ScaleY, correction.TranslateY,
            0f, 0f, 1f
        ]);
        SetTransform(matrix);
    }
}
