using Android.Content;
using Android.Views;
using Android.Widget;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

/// <summary>
/// Hosts the efficient native camera preview and the YUV-rendered compatibility fallback in the
/// same panel, allowing session preflight to select either path without rebuilding the drive UI.
/// </summary>
internal sealed class PhysicalCameraPreviewView : FrameLayout
{
    public PhysicalCameraPreviewView(Context context) : base(context)
    {
        Native = new AspectRatioTextureView(context);
        Software = new PhysicalYuvPreviewView(context) { Visibility = ViewStates.Gone };
        AddView(Native, new LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent,
            GravityFlags.Center));
        AddView(Software, new LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent,
            GravityFlags.Center));
    }

    public AspectRatioTextureView Native { get; }
    public PhysicalYuvPreviewView Software { get; }

    public void UseNativePreview(bool useNative)
    {
        Native.Visibility = useNative ? ViewStates.Visible : ViewStates.Gone;
        Software.Visibility = useNative ? ViewStates.Gone : ViewStates.Visible;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            RemoveAllViews();
            Native.Dispose();
            Software.Dispose();
        }
        base.Dispose(disposing);
    }
}
