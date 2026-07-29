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

    public static readonly BindableProperty ShowDiagnosticsProperty = BindableProperty.Create(
        nameof(ShowDiagnostics),
        typeof(bool),
        typeof(AnalysisFrameOverlay),
        false,
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

    public bool ShowDiagnostics
    {
        get => (bool)GetValue(ShowDiagnosticsProperty);
        set => SetValue(ShowDiagnosticsProperty, value);
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var frame = AnalysisFrame;
        if (frame is null
            || frame.SourceWidth <= 0
            || frame.SourceHeight <= 0
            || dirtyRect.Width <= 0
            || dirtyRect.Height <= 0)
        {
            return;
        }

        var transform = AspectRatioTransform.Create(
            frame.SourceWidth,
            frame.SourceHeight,
            dirtyRect.Width,
            dirtyRect.Height,
            AspectScaleMode.Fit,
            dirtyRect.Left,
            dirtyRect.Top);
        if (ShowDiagnostics && frame.Diagnostics is { } candidateDiagnostics)
        {
            foreach (var candidate in candidateDiagnostics.Frame.Candidates)
            {
                DrawCandidate(canvas, candidate, transform);
            }
        }
        foreach (var read in frame.Reads)
        {
            DrawBox(canvas, read.Bounds, read.Text, false, transform);
        }
        foreach (var confirmation in frame.Confirmations)
        {
            DrawBox(canvas, confirmation.Bounds, confirmation.DisplayPlate, true, transform);
        }
        if (ShowDiagnostics && frame.Diagnostics is { } diagnostics)
        {
            foreach (var track in diagnostics.Tracks)
            {
                DrawTrack(canvas, track, transform);
            }
        }
    }

    private static void DrawCandidate(
        ICanvas canvas,
        PlateCandidateDiagnostics candidate,
        AspectRatioTransform transform)
    {
        if (candidate.Detection.Bounds.IsEmpty)
        {
            return;
        }

        var projected = transform.Project(candidate.Detection.Bounds);
        var rectangle = new RectF(projected.Left, projected.Top, projected.Width, projected.Height);
        canvas.StrokeColor = Color.FromArgb("#55A7FF");
        canvas.StrokeSize = 2;
        canvas.DrawRoundedRectangle(rectangle, 6);
        if (!string.IsNullOrWhiteSpace(candidate.ReadText))
        {
            return;
        }

        var label = candidate.OcrAttempted
            ? $"det {candidate.Detection.Confidence:P0} · OCR no text"
            : $"det {candidate.Detection.Confidence:P0} · OCR skipped";
        var labelWidth = Math.Max(140, rectangle.Width);
        var labelTop = rectangle.Top >= 26 ? rectangle.Top - 24 : rectangle.Bottom + 4;
        canvas.FontColor = Colors.White;
        canvas.FontSize = 11;
        canvas.FillColor = Color.FromArgb("#E8172A42");
        canvas.FillRoundedRectangle(rectangle.Left, labelTop, labelWidth, 20, 5);
        canvas.DrawString(label, rectangle.Left + 6, labelTop, labelWidth - 12, 20, HorizontalAlignment.Left, VerticalAlignment.Center);
    }

    private static void DrawTrack(ICanvas canvas, PlateTrackSnapshot track, AspectRatioTransform transform)
    {
        if (track.Bounds.IsEmpty)
        {
            return;
        }

        var projected = transform.Project(track.Bounds);
        var rectangle = new RectF(projected.Left, projected.Top, projected.Width, projected.Height);
        var accent = Color.FromArgb("#D77BFF");
        canvas.StrokeColor = accent;
        canvas.StrokeSize = 2;
        canvas.StrokeDashPattern = [6, 4];
        canvas.DrawRoundedRectangle(rectangle, 6);
        canvas.StrokeDashPattern = null;

        var label = $"T{track.TrackId.ToString("N")[..6]} · {track.ObservationCount} obs · {track.LastRead}";
        var labelWidth = Math.Max(150, rectangle.Width);
        var labelTop = rectangle.Top >= 52 ? rectangle.Top - 50 : rectangle.Bottom + 30;
        canvas.FontColor = Colors.White;
        canvas.FontSize = 11;
        canvas.Font = Microsoft.Maui.Graphics.Font.Default;
        canvas.FillColor = Color.FromArgb("#E834163D");
        canvas.FillRoundedRectangle(rectangle.Left, labelTop, labelWidth, 22, 5);
        canvas.DrawString(label, rectangle.Left + 6, labelTop, labelWidth - 12, 22, HorizontalAlignment.Left, VerticalAlignment.Center);
    }

    private static void DrawBox(
        ICanvas canvas,
        BoundingBox bounds,
        string label,
        bool confirmed,
        AspectRatioTransform transform)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        var projected = transform.Project(bounds);
        var rectangle = new RectF(projected.Left, projected.Top, projected.Width, projected.Height);
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
