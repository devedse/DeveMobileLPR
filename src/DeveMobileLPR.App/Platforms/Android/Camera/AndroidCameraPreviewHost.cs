using Android.Content;
using Android.Views;
using Android.Widget;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

/// <summary>Android visual host only; camera acquisition is composed by the input factory.</summary>
internal sealed class AndroidCameraPreviewHost : FrameLayout
{
    public AndroidCameraPreviewHost(Context context) : base(context)
    {
        PreviewGrid = new LinearLayout(context) { Orientation = Orientation.Vertical };
        AddView(PreviewGrid, new LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));
        NetworkPreview = new AndroidVideoTextureView(context);
    }

    public LinearLayout PreviewGrid { get; }
    public AndroidVideoTextureView NetworkPreview { get; }
}
