using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.Camera.View;
using AndroidX.Lifecycle;
using DeveMobileLPR.App.Camera;
using DeveMobileLPR.App.Controls;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;
using DeveMobileLPR.Geometry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;

namespace DeveMobileLPR.App;

internal sealed class CameraPreviewHandler : ViewHandler<CameraPreview, FrameLayout>
{
    private const AspectScaleMode PreviewScaleMode = AspectScaleMode.Fill;
    public static readonly IPropertyMapper<CameraPreview, CameraPreviewHandler> Mapper =
        new PropertyMapper<CameraPreview, CameraPreviewHandler>(ViewHandler.ViewMapper);

    private AndroidDriveFrameSource? _source;
    private DriveCoordinator? _coordinator;
    private DetectionOverlayView? _overlay;

    public CameraPreviewHandler() : base(Mapper)
    {
    }

    protected override FrameLayout CreatePlatformView()
    {
        var context = MauiContext?.Context ?? throw new InvalidOperationException("Android context is unavailable.");
        var activity = Platform.CurrentActivity as ILifecycleOwner
            ?? throw new InvalidOperationException("The active Android activity is not a CameraX lifecycle owner.");
        _coordinator = MauiContext!.Services.GetRequiredService<DriveCoordinator>();
        var settings = MauiContext.Services.GetRequiredService<AppSettings>();
        var root = new FrameLayout(context);
        var match = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        var preview = new PreviewView(context);
        preview.SetScaleType(GetPreviewScaleType());
        // Compatible uses TextureView, which guarantees the custom detection layer composites above it.
        preview.SetImplementationMode(PreviewView.ImplementationMode.Compatible);
        root.AddView(preview, match);
        var streamPreview = new AndroidVideoTextureView(context)
        {
            Visibility = ViewStates.Gone
        };
        root.AddView(streamPreview, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent)
        {
            Gravity = GravityFlags.Center
        });
        _overlay = new DetectionOverlayView(
            context,
            () => settings.ShowRoadGuide,
            () => _coordinator?.Snapshot.SelectedCameraId == DriveInputIds.NetworkLlHls
                ? AspectScaleMode.Fit
                : PreviewScaleMode);
        root.AddView(_overlay, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _source = new AndroidDriveFrameSource(
            context,
            activity,
            preview,
            streamPreview,
            settings.NetworkStreamUrl,
            () => settings.RecognitionFramesPerSecond,
            frame => _coordinator.SubmitFrame(frame));
        _coordinator.AttachCamera(_source);
        _coordinator.SnapshotChanged += SnapshotChanged;
        _overlay.Update(_coordinator.Snapshot);
        return root;
    }

    private void SnapshotChanged(object? sender, DriveSnapshot snapshot) => _overlay?.Update(snapshot);

    private static PreviewView.ScaleType GetPreviewScaleType() => PreviewScaleMode switch
    {
        AspectScaleMode.Fit => PreviewView.ScaleType.FitCenter!,
        AspectScaleMode.Fill => PreviewView.ScaleType.FillCenter!,
        _ => throw new ArgumentOutOfRangeException()
    };

    protected override void DisconnectHandler(FrameLayout platformView)
    {
        if (_coordinator is not null)
        {
            _coordinator.SnapshotChanged -= SnapshotChanged;
        }
        if (_source is not null)
        {
            _coordinator?.DetachCamera(_source);
            _ = DisposeSourceAsync(_source);
            _source = null;
        }
        _overlay = null;
        _coordinator = null;
        base.DisconnectHandler(platformView);
    }

    private static async Task DisposeSourceAsync(AndroidDriveFrameSource source)
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
