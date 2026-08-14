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
    private int _uprightContentWidth;
    private int _uprightContentHeight;
    private int _rotationDegrees;
    private AspectScaleMode _scaleMode;
    private bool _mirrorHorizontally;

    public CameraSurfaceTransform? AppliedTransform { get; private set; }

    public void ConfigureBuffer(
        int bufferWidth,
        int bufferHeight,
        int uprightContentWidth,
        int uprightContentHeight,
        int rotationDegrees,
        AspectScaleMode scaleMode,
        bool mirrorHorizontally)
    {
        _bufferWidth = bufferWidth;
        _bufferHeight = bufferHeight;
        _uprightContentWidth = uprightContentWidth;
        _uprightContentHeight = uprightContentHeight;
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

        // TextureView has already stretched the producer buffer to this view. Work backwards
        // from the desired final upright rectangle: for a quarter turn the producer must be
        // shaped portrait-first so that the rotation finishes as an undistorted landscape image.
        var correction = CameraSurfaceTransform.Create(
            _uprightContentWidth,
            _uprightContentHeight,
            Width,
            Height,
            _rotationDegrees,
            _scaleMode);
        AppliedTransform = correction;
        using var matrix = new Matrix();
        var centerX = Width / 2f;
        var centerY = Height / 2f;
        matrix.SetScale(correction.ProducerScaleX, correction.ProducerScaleY, centerX, centerY);
        matrix.PostRotate(correction.ClockwiseRotationDegrees, centerX, centerY);
        if (_mirrorHorizontally)
        {
            matrix.PostScale(-1f, 1f, centerX, centerY);
        }
        SetTransform(matrix);
    }
}
