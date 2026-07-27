using DeveMobileLPR.App.Controls;
using DeveMobileLPR.App.Platforms.Windows;
using DeveMobileLPR.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;
using WinUIGrid = Microsoft.UI.Xaml.Controls.Grid;
using MediaPlayerElement = Microsoft.UI.Xaml.Controls.MediaPlayerElement;

namespace DeveMobileLPR.App;

internal sealed class CameraPreviewHandler : ViewHandler<CameraPreview, WinUIGrid>
{
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
            Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill
        };
        root.Children.Add(preview);
        _source = new WindowsWebcamFrameSource(preview, frame => _coordinator.SubmitFrame(frame));
        _coordinator.AttachCamera(_source);
        _ = InitializeSourceAsync(_source, settings.CameraId);
        return root;
    }

    private static async Task InitializeSourceAsync(WindowsWebcamFrameSource source, string cameraId)
    {
        try
        {
            await source.InitializeAsync(cameraId);
        }
        catch (Exception exception)
        {
            source.ReportInitializationFailure(exception);
        }
    }

    protected override void DisconnectHandler(WinUIGrid platformView)
    {
        if (_source is not null)
        {
            _coordinator?.DetachCamera(_source);
            _ = _source.DisposeAsync().AsTask();
            _source = null;
        }
        _coordinator = null;
        base.DisconnectHandler(platformView);
    }
}