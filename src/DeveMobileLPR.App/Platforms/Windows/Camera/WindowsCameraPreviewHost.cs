using MediaPlayerElement = Microsoft.UI.Xaml.Controls.MediaPlayerElement;
using WinUIGrid = Microsoft.UI.Xaml.Controls.Grid;
using WinUIImage = Microsoft.UI.Xaml.Controls.Image;

namespace DeveMobileLPR.App.Platforms.Windows.Camera;

/// <summary>WinUI visual host only; camera acquisition is composed by the input factory.</summary>
internal sealed class WindowsCameraPreviewHost : WinUIGrid
{
    public WindowsCameraPreviewHost()
    {
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            global::Windows.UI.Color.FromArgb(255, 11, 13, 18));
        WebcamPreview = new MediaPlayerElement
        {
            AreTransportControlsEnabled = false,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
        };
        Children.Add(WebcamPreview);
        NetworkPreview = new WinUIImage
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            Visibility = Microsoft.UI.Xaml.Visibility.Collapsed
        };
        Children.Add(NetworkPreview);
    }

    public MediaPlayerElement WebcamPreview { get; }
    public WinUIImage NetworkPreview { get; }
}
