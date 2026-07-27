using DeveMobileLPR.App.Controls;
using Microsoft.Maui.Handlers;
using WinUIGrid = Microsoft.UI.Xaml.Controls.Grid;

namespace DeveMobileLPR.App;

internal sealed class CameraPreviewHandler : ViewHandler<CameraPreview, WinUIGrid>
{
    public static readonly IPropertyMapper<CameraPreview, CameraPreviewHandler> Mapper =
        new PropertyMapper<CameraPreview, CameraPreviewHandler>(ViewHandler.ViewMapper);

    public CameraPreviewHandler() : base(Mapper)
    {
    }

    protected override WinUIGrid CreatePlatformView() => new()
    {
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 11, 13, 16))
    };
}