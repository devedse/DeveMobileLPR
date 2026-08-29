using Android.Content;
using Android.Graphics;
using Android.Views;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class UvcPreviewTextureView : TextureView, TextureView.ISurfaceTextureListener
{
    private Surface? _surface;
    private float _zoomRatio = 1f;

    public UvcPreviewTextureView(Context context) : base(context)
    {
        SurfaceTextureListener = this;
    }

    public event EventHandler<Surface?>? SurfaceChanged;

    public Surface? PreviewSurface => _surface;

    public void SetZoom(float zoomRatio)
    {
        _zoomRatio = Math.Clamp(zoomRatio, 1f, 4f);
        ApplyZoomTransform();
    }

    private void ApplyZoomTransform()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        using var matrix = new Matrix();
        matrix.SetScale(_zoomRatio, _zoomRatio, Width / 2f, Height / 2f);
        SetTransform(matrix);
    }

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
    {
        ReplaceSurface(new Surface(surface));
        ApplyZoomTransform();
    }

    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
    {
        ReplaceSurface(null);
        return true;
    }

    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
    {
        ApplyZoomTransform();
    }

    public void OnSurfaceTextureUpdated(SurfaceTexture surface)
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SurfaceTextureListener = null;
            ReplaceSurface(null);
        }
        base.Dispose(disposing);
    }

    private void ReplaceSurface(Surface? surface)
    {
        var previous = _surface;
        _surface = surface;
        SurfaceChanged?.Invoke(this, surface);
        previous?.Dispose();
    }
}
