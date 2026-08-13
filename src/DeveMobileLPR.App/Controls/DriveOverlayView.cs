using DeveMobileLPR.Application;
using DeveMobileLPR.Geometry;
using GraphicsFont = Microsoft.Maui.Graphics.Font;

namespace DeveMobileLPR.App.Controls;

/// <summary>
/// Draws detection boxes and plate labels over a video surface. This is the only overlay renderer
/// in the app: the live drive view and analysis review both feed it <see cref="DriveOverlay"/>
/// lists, so a detection is projected, styled, and labelled identically in both.
/// </summary>
internal sealed class DriveOverlayView : GraphicsView, IDrawable
{
    private const float LabelHeight = 46;
    private const float LabelTextRowHeight = 20;
    private const float LabelTextTopInset = 3;
    private const float LabelPadding = 10;
    private const float LabelCornerRadius = 10;
    private const float LabelGap = 5;
    private const float EdgeMargin = 8;
    private const float TitleFontSize = 14;
    private const float DetailFontSize = 11;
    private const float GuideTopFraction = 0.18f;
    private const float GuideBottomFraction = 0.94f;
    private const float GuideBracketLength = 42;

    public static readonly BindableProperty OverlaysProperty = BindableProperty.Create(
        nameof(Overlays),
        typeof(IReadOnlyList<DriveOverlay>),
        typeof(DriveOverlayView),
        propertyChanged: static (bindable, _, _) => ((DriveOverlayView)bindable).Invalidate());

    public static readonly BindableProperty ShowGuideProperty = BindableProperty.Create(
        nameof(ShowGuide),
        typeof(bool),
        typeof(DriveOverlayView),
        false,
        propertyChanged: static (bindable, _, _) => ((DriveOverlayView)bindable).Invalidate());

    public static readonly BindableProperty ScaleModeProperty = BindableProperty.Create(
        nameof(ScaleMode),
        typeof(AspectScaleMode),
        typeof(DriveOverlayView),
        AspectScaleMode.Fit,
        propertyChanged: static (bindable, _, _) => ((DriveOverlayView)bindable).Invalidate());

    public static readonly BindableProperty SourceIdsProperty = BindableProperty.Create(
        nameof(SourceIds),
        typeof(IReadOnlyList<string>),
        typeof(DriveOverlayView),
        propertyChanged: static (bindable, _, _) => ((DriveOverlayView)bindable).Invalidate());

    public DriveOverlayView()
    {
        Drawable = this;
        InputTransparent = true;
    }

    /// <summary>Overlays in draw order; producers order them by kind so confirmations sit on top.</summary>
    public IReadOnlyList<DriveOverlay>? Overlays
    {
        get => (IReadOnlyList<DriveOverlay>?)GetValue(OverlaysProperty);
        set => SetValue(OverlaysProperty, value);
    }

    /// <summary>Draws the framing brackets that help a driver aim the phone at the road.</summary>
    public bool ShowGuide
    {
        get => (bool)GetValue(ShowGuideProperty);
        set => SetValue(ShowGuideProperty, value);
    }

    /// <summary>Must match how the surface underneath fits the source frame, or boxes drift.</summary>
    public AspectScaleMode ScaleMode
    {
        get => (AspectScaleMode)GetValue(ScaleModeProperty);
        set => SetValue(ScaleModeProperty, value);
    }

    /// <summary>Source order used by the native preview's one- or two-column grid.</summary>
    public IReadOnlyList<string>? SourceIds
    {
        get => (IReadOnlyList<string>?)GetValue(SourceIdsProperty);
        set => SetValue(SourceIdsProperty, value);
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (dirtyRect.Width <= 0 || dirtyRect.Height <= 0)
        {
            return;
        }

        if (ShowGuide)
        {
            DrawGuide(canvas, dirtyRect);
        }

        var overlays = Overlays;
        if (overlays is null)
        {
            return;
        }

        foreach (var overlay in overlays)
        {
            DrawOverlay(canvas, dirtyRect, overlay);
        }
    }

    private static void DrawGuide(ICanvas canvas, RectF bounds)
    {
        var left = bounds.Left;
        var right = bounds.Right;
        var top = bounds.Top + bounds.Height * GuideTopFraction;
        var bottom = bounds.Top + bounds.Height * GuideBottomFraction;
        var length = Math.Min(GuideBracketLength, Math.Min(right - left, bottom - top) / 5);
        canvas.StrokeColor = Color.FromRgba(255, 255, 255, 165);
        canvas.StrokeSize = 2;
        foreach (var (x1, y1, x2, y2) in new[]
        {
            (left, top, left + length, top), (left, top, left, top + length),
            (right, top, right - length, top), (right, top, right, top + length),
            (left, bottom, left + length, bottom), (left, bottom, left, bottom - length),
            (right, bottom, right - length, bottom), (right, bottom, right, bottom - length)
        })
        {
            canvas.DrawLine(x1, y1, x2, y2);
        }
    }

    private void DrawOverlay(ICanvas canvas, RectF viewport, DriveOverlay overlay)
    {
        if (!TryGetSourceViewport(viewport, overlay.SourceId, out viewport))
        {
            return;
        }
        if (!DriveOverlayLayout.TryProject(
                overlay,
                viewport.Width,
                viewport.Height,
                ScaleMode,
                out var projected))
        {
            return;
        }

        var style = DriveOverlayStyle.For(overlay.Kind);
        var box = new RectF(
            viewport.Left + projected.Left,
            viewport.Top + projected.Top,
            projected.Width,
            projected.Height);
        canvas.StrokeColor = style.Accent;
        canvas.StrokeSize = style.StrokeSize;
        canvas.StrokeDashPattern = style.StrokeDashPattern;
        canvas.DrawRoundedRectangle(box, DriveOverlayStyle.CornerRadius);
        canvas.StrokeDashPattern = null;

        DrawLabel(canvas, viewport, box, overlay, style);
    }

    private bool TryGetSourceViewport(RectF fullViewport, string sourceId, out RectF viewport)
    {
        var sourceIds = SourceIds;
        if (sourceIds is null || sourceIds.Count <= 1)
        {
            viewport = fullViewport;
            return true;
        }

        var index = -1;
        for (var candidate = 0; candidate < sourceIds.Count; candidate++)
        {
            if (string.Equals(sourceIds[candidate], sourceId, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }
        if (index < 0)
        {
            viewport = default;
            return false;
        }

        const int columns = 2;
        var rows = (sourceIds.Count + columns - 1) / columns;
        var cellWidth = fullViewport.Width / columns;
        var cellHeight = fullViewport.Height / rows;
        viewport = new RectF(
            fullViewport.Left + index % columns * cellWidth,
            fullViewport.Top + index / columns * cellHeight,
            cellWidth,
            cellHeight);
        return true;
    }

    private static void DrawLabel(
        ICanvas canvas,
        RectF viewport,
        RectF box,
        DriveOverlay overlay,
        DriveOverlayStyle style)
    {
        var titleFont = GraphicsFont.DefaultBold;
        var detailFont = GraphicsFont.Default;
        var textWidth = Math.Max(
            canvas.GetStringSize(overlay.Title, titleFont, TitleFontSize).Width,
            canvas.GetStringSize(overlay.Detail, detailFont, DetailFontSize).Width);
        var maximumWidth = Math.Max(1, viewport.Width - EdgeMargin * 2);
        var labelWidth = Math.Min(maximumWidth, Math.Max(box.Width, textWidth + LabelPadding * 2));
        var labelLeft = Math.Clamp(
            box.Left,
            viewport.Left + EdgeMargin,
            Math.Max(viewport.Left + EdgeMargin, viewport.Right - labelWidth - EdgeMargin));
        var labelTop = box.Top - LabelHeight - LabelGap;
        if (labelTop < viewport.Top + EdgeMargin)
        {
            labelTop = box.Bottom + LabelGap;
        }

        canvas.FillColor = style.LabelBackground;
        canvas.FillRoundedRectangle(labelLeft, labelTop, labelWidth, LabelHeight, LabelCornerRadius);

        var textLeft = labelLeft + LabelPadding;
        var textWidthAvailable = labelWidth - LabelPadding * 2;
        canvas.Font = titleFont;
        canvas.FontSize = TitleFontSize;
        canvas.FontColor = style.TitleColor;
        canvas.DrawString(
            Ellipsize(canvas, overlay.Title, titleFont, TitleFontSize, textWidthAvailable),
            textLeft,
            labelTop + LabelTextTopInset,
            textWidthAvailable,
            LabelTextRowHeight,
            HorizontalAlignment.Left,
            VerticalAlignment.Center);
        canvas.Font = detailFont;
        canvas.FontSize = DetailFontSize;
        canvas.FontColor = style.DetailColor;
        canvas.DrawString(
            Ellipsize(canvas, overlay.Detail, detailFont, DetailFontSize, textWidthAvailable),
            textLeft,
            labelTop + LabelTextTopInset + LabelTextRowHeight,
            textWidthAvailable,
            LabelTextRowHeight,
            HorizontalAlignment.Left,
            VerticalAlignment.Center);
    }

    private static string Ellipsize(ICanvas canvas, string value, IFont font, float fontSize, float width)
    {
        if (string.IsNullOrEmpty(value) || canvas.GetStringSize(value, font, fontSize).Width <= width)
        {
            return value;
        }

        const string suffix = "…";
        var trimmed = value;
        while (trimmed.Length > 1
            && canvas.GetStringSize(trimmed + suffix, font, fontSize).Width > width)
        {
            trimmed = trimmed[..^1];
        }

        return trimmed + suffix;
    }
}
