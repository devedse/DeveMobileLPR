using DeveMobileLPR.App.Controls;
using DeveMobileLPR.App.Platforms.Windows;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;
using DeveMobileLPR.Geometry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;
using WinUIGrid = Microsoft.UI.Xaml.Controls.Grid;
using WinUIImage = Microsoft.UI.Xaml.Controls.Image;
using MediaPlayerElement = Microsoft.UI.Xaml.Controls.MediaPlayerElement;

namespace DeveMobileLPR.App;

internal sealed class CameraPreviewHandler : ViewHandler<CameraPreview, WinUIGrid>
{
    /// <summary>Desktop windows are an arbitrary shape, so both webcam and stream frames letterbox.</summary>
    private const AspectScaleMode PreviewScaleMode = AspectScaleMode.Fit;

    private WindowsWebcamFrameSource? _source;
    private DriveCoordinator? _coordinator;

    public static readonly IPropertyMapper<CameraPreview, CameraPreviewHandler> Mapper =
        new PropertyMapper<CameraPreview, CameraPreviewHandler>(ViewHandler.ViewMapper);

    public CameraPreviewHandler() : base(Mapper)
    {
    }

    protected override WinUIGrid CreatePlatformView()
    {
        _coordinator = MauiContext!.Services.GetRequiredService<DriveCoordinator>();
        var settings = MauiContext.Services.GetRequiredService<AppSettings>();
        var root = new WinUIGrid
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 11, 13, 16))
        };
        var preview = new MediaPlayerElement
        {
            AreTransportControlsEnabled = false,
            Stretch = GetPreviewStretch()
        };
        root.Children.Add(preview);
        var streamPreview = new WinUIImage
        {
            Stretch = GetPreviewStretch(),
            Visibility = Microsoft.UI.Xaml.Visibility.Collapsed
        };
        root.Children.Add(streamPreview);
        _source = new WindowsWebcamFrameSource(
            preview,
            streamPreview,
            settings.NetworkStreamUrl,
            () => settings.RecognitionFramesPerSecond,
            () => _coordinator.HasPendingRecognitionFrame,
            frame => _coordinator.SubmitFrame(frame));
        _coordinator.AttachCamera(_source);
        return root;
    }

    protected override void ConnectHandler(WinUIGrid platformView)
    {
        base.ConnectHandler(platformView);
        VirtualView.CameraScaleMode = PreviewScaleMode;
        VirtualView.StreamScaleMode = PreviewScaleMode;
    }

    private static Microsoft.UI.Xaml.Media.Stretch GetPreviewStretch() => PreviewScaleMode switch
    {
        AspectScaleMode.Fit => Microsoft.UI.Xaml.Media.Stretch.Uniform,
        AspectScaleMode.Fill => Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
        _ => throw new ArgumentOutOfRangeException()
    };

    protected override void DisconnectHandler(WinUIGrid platformView)
    {
        if (_source is not null)
        {
            var source = _source;
            source.DeactivatePreview();
            _coordinator?.DetachCamera(source);
            _source = null;
            _ = DisposeSourceAsync(source);
        }
        _coordinator = null;
        base.DisconnectHandler(platformView);
    }

    private static async Task DisposeSourceAsync(WindowsWebcamFrameSource source)
    {
        try
        {
            await source.DisposeAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Windows camera cleanup failed: {exception}");
        }
    }
}
