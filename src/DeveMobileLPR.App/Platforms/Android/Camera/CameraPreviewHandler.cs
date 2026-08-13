using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.Camera.View;
using AndroidX.Lifecycle;
using DeveMobileLPR.App.Controls;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;
using DeveMobileLPR.Geometry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class CameraPreviewHandler : ViewHandler<CameraPreview, FrameLayout>
{
    /// <summary>CameraX frames fill the viewport; the phone is mounted, so cropping beats letterboxing.</summary>
    private const AspectScaleMode CameraScaleMode = AspectScaleMode.Fill;

    /// <summary>Network streams carry an arbitrary aspect, so <see cref="AndroidVideoTextureView"/> letterboxes them.</summary>
    private const AspectScaleMode StreamScaleMode = AspectScaleMode.Fit;

    public static readonly IPropertyMapper<CameraPreview, CameraPreviewHandler> Mapper =
        new PropertyMapper<CameraPreview, CameraPreviewHandler>(ViewHandler.ViewMapper);

    private AndroidDriveVideoInput? _source;
    private DriveCoordinator? _coordinator;

    public CameraPreviewHandler() : base(Mapper)
    {
    }

    protected override FrameLayout CreatePlatformView()
    {
        var context = MauiContext?.Context ?? throw new InvalidOperationException("Android context is unavailable.");
        var lifecycleOwner = MauiContext!.Services.GetRequiredService<AndroidCameraLifecycleOwner>();
        _coordinator = MauiContext!.Services.GetRequiredService<DriveCoordinator>();
        var settings = MauiContext.Services.GetRequiredService<AppSettings>();
        var sourceCatalog = MauiContext.Services.GetRequiredService<IDriveSourceCatalog>();
        var root = new FrameLayout(context);
        var previewGrid = new LinearLayout(context)
        {
            Orientation = Orientation.Vertical
        };
        root.AddView(previewGrid, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));
        var streamPreview = new AndroidVideoTextureView(context);
        _source = new AndroidDriveVideoInput(
            context,
            lifecycleOwner,
            sourceCatalog,
            previewGrid,
            streamPreview,
            settings.NetworkStreamUrl,
            () => settings.RecognitionFramesPerSecond,
            sourceId => _coordinator.HasPendingRecognitionFrameFor(sourceId),
            (sourceId, frame) => _coordinator.SubmitFrame(sourceId, frame));
        _coordinator.AttachCamera(_source);
        return root;
    }
    protected override void ConnectHandler(FrameLayout platformView)
    {
        base.ConnectHandler(platformView);
        VirtualView.CameraScaleMode = CameraScaleMode;
        VirtualView.StreamScaleMode = StreamScaleMode;
    }

    private static PreviewView.ScaleType GetPreviewScaleType() => CameraScaleMode switch
    {
        AspectScaleMode.Fit => PreviewView.ScaleType.FitCenter!,
        AspectScaleMode.Fill => PreviewView.ScaleType.FillCenter!,
        _ => throw new ArgumentOutOfRangeException()
    };

    protected override void DisconnectHandler(FrameLayout platformView)
    {
        if (_source is not null)
        {
            _coordinator?.DetachCamera(_source);
            _ = DisposeSourceAsync(_source);
            _source = null;
        }
        _coordinator = null;
        base.DisconnectHandler(platformView);
    }

    private static async Task DisposeSourceAsync(AndroidDriveVideoInput source)
    {
        try
        {
            await source.DisposeAsync();
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("DeveMobileLPR.Video", $"Android video cleanup failed: {exception}");
        }
    }
}
