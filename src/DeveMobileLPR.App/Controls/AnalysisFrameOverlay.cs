using DeveMobileLPR.Geometry;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.Controls;

internal sealed class AnalysisFrameOverlay : GraphicsView, IDrawable
{
    public static readonly BindableProperty AnalysisFrameProperty = BindableProperty.Create(
        nameof(AnalysisFrame),
        typeof(AnalyzedVideoFrame),
        typeof(AnalysisFrameOverlay),
        propertyChanged: static (bindable, _, _) => ((AnalysisFrameOverlay)bindable).Invalidate());

    public AnalysisFrameOverlay()
    {
        Drawable = this;
        InputTransparent = true;
    }

    public AnalyzedVideoFrame? AnalysisFrame
    {
        get => (AnalyzedVideoFrame?)GetValue(AnalysisFrameProperty);
        set => SetValue(AnalysisFrameProperty, value);
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var frame = AnalysisFrame;
        if (frame is null || frame.SourceWidth <= 0 || frame.SourceHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(dirtyRect.Width / frame.SourceWidth, dirtyRect.Height / frame.SourceHeight);
        var offsetX = dirtyRect.Left + (dirtyRect.Width - frame.SourceWidth * scale) / 2;
        var offsetY = dirtyRect.Top + (dirtyRect.Height - frame.SourceHeight * scale) / 2;
        foreach (var read in frame.Reads)
        {
            DrawBox(canvas, read.Bounds, read.Text, false, scale, offsetX, offsetY);
        }
        foreach (var confirmation in frame.Confirmations)
        {
            DrawBox(canvas, confirmation.Bounds, confirmation.DisplayPlate, true, scale, offsetX, offsetY);
        }
    }

    private static void DrawBox(
        ICanvas canvas,
        BoundingBox bounds,
        string label,
        bool confirmed,
        float scale,
        float offsetX,
        float offsetY)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        var rectangle = new RectF(
            bounds.Left * scale + offsetX,
            bounds.Top * scale + offsetY,
            bounds.Width * scale,
            bounds.Height * scale);
        var accent = Color.FromArgb(confirmed ? "#F6C945" : "#58E0C2");
        canvas.StrokeColor = accent;
        canvas.StrokeSize = confirmed ? 4 : 3;
        canvas.DrawRoundedRectangle(rectangle, 6);

        canvas.FontColor = confirmed ? Color.FromArgb("#141105") : Colors.White;
        canvas.FontSize = 13;
        canvas.Font = Microsoft.Maui.Graphics.Font.DefaultBold;
        var labelWidth = Math.Max(88, rectangle.Width);
        var labelTop = rectangle.Top >= 30 ? rectangle.Top - 26 : rectangle.Bottom + 4;
        canvas.FillColor = confirmed ? accent : Color.FromArgb("#E80B0D10");
        canvas.FillRoundedRectangle(rectangle.Left, labelTop, labelWidth, 24, 5);
        canvas.DrawString(label, rectangle.Left + 7, labelTop, labelWidth - 14, 24, HorizontalAlignment.Left, VerticalAlignment.Center);
    }
}