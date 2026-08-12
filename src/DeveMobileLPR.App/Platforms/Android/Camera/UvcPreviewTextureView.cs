using Android.Content;
using Android.Graphics;
using Android.Views;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class UvcPreviewTextureView : TextureView, TextureView.ISurfaceTextureListener
{
    private Surface? _surface;

    public UvcPreviewTextureView(Context context) : base(context)
    {
        SurfaceTextureListener = this;
    }

    public event EventHandler<Surface?>? SurfaceChanged;

    public Surface? PreviewSurface => _surface;

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
    {
        ReplaceSurface(new Surface(surface));
    }

    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
    {
        ReplaceSurface(null);
        return true;
    }

    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
    {
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
