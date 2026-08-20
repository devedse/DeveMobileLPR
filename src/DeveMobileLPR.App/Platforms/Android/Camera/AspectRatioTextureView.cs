using Android.Content;
using Android.Graphics;
using Android.Views;
using DeveMobileLPR.Geometry;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

/// <summary>Displays a native camera buffer without changing its proportions.</summary>
internal sealed class AspectRatioTextureView : TextureView, TextureView.ISurfaceTextureListener
{
    private int _bufferWidth;
    private int _bufferHeight;
    private int _uprightContentWidth;
    private int _uprightContentHeight;
    private int _rotationDegrees;
    private AspectScaleMode _scaleMode;
    private bool _mirrorHorizontally;

    public AspectRatioTextureView(Context context) : base(context)
    {
        SurfaceTextureListener = this;
    }

    public event Action? FramePresented;

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

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) =>
        ApplyAspectTransform();

    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface) => true;

    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) =>
        ApplyAspectTransform();

    public void OnSurfaceTextureUpdated(SurfaceTexture surface) => FramePresented?.Invoke();

    private void ApplyAspectTransform()
    {
        if (Width <= 0 || Height <= 0 || _bufferWidth <= 0 || _bufferHeight <= 0)
        {
            return;
        }

        var correction = CameraSurfaceTransform.Create(
            _uprightContentWidth,
            _uprightContentHeight,
            Width,
            Height,
            _rotationDegrees,
            _scaleMode);
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
