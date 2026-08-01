using DeveMobileLPR.Application;
using DeveMobileLPR.Geometry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinUIColor = Windows.UI.Color;
using WinUISolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;

namespace DeveMobileLPR.App.Platforms.Windows;

internal sealed class WindowsDetectionOverlay : Canvas
{
    private static readonly WinUISolidColorBrush ReadingBrush = new(WinUIColor.FromArgb(255, 88, 224, 194));
    private static readonly WinUISolidColorBrush ConfirmedBrush = new(WinUIColor.FromArgb(255, 245, 197, 66));
    private static readonly WinUISolidColorBrush TrackBrush = new(WinUIColor.FromArgb(255, 215, 123, 255));
    private static readonly WinUISolidColorBrush ReadingLabelBrush = new(WinUIColor.FromArgb(232, 11, 13, 16));
    private static readonly WinUISolidColorBrush ConfirmedLabelBrush = new(WinUIColor.FromArgb(242, 245, 197, 66));
    private static readonly WinUISolidColorBrush LightTextBrush = new(WinUIColor.FromArgb(255, 247, 249, 252));
    private static readonly WinUISolidColorBrush DarkTextBrush = new(WinUIColor.FromArgb(255, 20, 17, 5));
    private readonly AspectScaleMode _scaleMode;
    private IReadOnlyList<DriveOverlay> _overlays = [];

    public WindowsDetectionOverlay(AspectScaleMode scaleMode)
    {
        _scaleMode = scaleMode;
        IsHitTestVisible = false;
        SizeChanged += (_, _) => Render();
    }

    public void Update(DriveSnapshot snapshot)
    {
        _overlays = DriveOverlayLayout.GetVisibleOverlays(snapshot);
        Render();
    }

    private void Render()
    {
        Children.Clear();
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        foreach (var overlay in _overlays.OrderBy(static item => item.Kind))
        {
            DrawDetection(overlay);
        }
    }

    private void DrawDetection(DriveOverlay overlay)
    {
        if (!DriveOverlayLayout.TryProject(
                overlay,
                (float)ActualWidth,
                (float)ActualHeight,
                _scaleMode,
                out var projected))
        {
            return;
        }
        var left = projected.Left;
        var top = projected.Top;
        var width = projected.Width;
        var height = projected.Height;
        var confirmed = overlay.Kind == DriveOverlayKind.Confirmed;
        var accent = overlay.Kind == DriveOverlayKind.Track ? TrackBrush : confirmed ? ConfirmedBrush : ReadingBrush;
        var box = new Rectangle
        {
            Width = width,
            Height = height,
            RadiusX = 8,
            RadiusY = 8,
            Stroke = accent,
            StrokeThickness = confirmed ? 3.5 : 2.25,
            StrokeDashArray = overlay.Kind == DriveOverlayKind.Track
                ? new Microsoft.UI.Xaml.Media.DoubleCollection { 5, 3 }
                : null
        };
        SetLeft(box, left);
        SetTop(box, top);
        Children.Add(box);

        var labelWidth = Math.Clamp(Math.Max(width, 150), 150, Math.Max(150, ActualWidth - 16));
        const double labelHeight = 46;
        var labelLeft = Math.Clamp(left, 8, Math.Max(8, ActualWidth - labelWidth - 8));
        var labelTop = top - labelHeight - 5;
        if (labelTop < 8) labelTop = top + height + 5;
        var textColor = confirmed ? DarkTextBrush : LightTextBrush;
        var label = new Microsoft.UI.Xaml.Controls.Border
        {
            Width = labelWidth,
            Height = labelHeight,
            Padding = new Microsoft.UI.Xaml.Thickness(9, 4, 9, 4),
            Background = confirmed ? ConfirmedLabelBrush : ReadingLabelBrush,
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(7),
            Child = new StackPanel
            {
                Spacing = 0,
                Children =
                {
                    new TextBlock { Text = overlay.Title, FontSize = 13, Foreground = textColor, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = overlay.Detail, FontSize = 10, Foreground = textColor, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            }
        };
        SetLeft(label, labelLeft);
        SetTop(label, labelTop);
        Children.Add(label);
    }
}
