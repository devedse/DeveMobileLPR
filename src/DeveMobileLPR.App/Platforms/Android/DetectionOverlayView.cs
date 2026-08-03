using Android.Content;
using Android.Graphics;
using Android.Views;
using DeveMobileLPR.Application;
using DeveMobileLPR.Geometry;
using AColor = Android.Graphics.Color;
using ARectF = Android.Graphics.RectF;
using AView = Android.Views.View;
using Paint = Android.Graphics.Paint;

namespace DeveMobileLPR.App;

internal sealed class DetectionOverlayView : AView
{
    private readonly Paint _boxPaint = new(PaintFlags.AntiAlias);
    private readonly Paint _labelPaint = new(PaintFlags.AntiAlias);
    private readonly Paint _textPaint = new(PaintFlags.AntiAlias);
    private readonly Paint _detailPaint = new(PaintFlags.AntiAlias);
    private readonly DashPathEffect _trackPathEffect;
    private readonly Func<bool> _showGuide;
    private readonly Func<AspectScaleMode> _scaleMode;
    private DriveSnapshot? _snapshot;
    private readonly float _density;

    public DetectionOverlayView(
        Context context,
        Func<bool> showGuide,
        Func<AspectScaleMode> scaleMode) : base(context)
    {
        _showGuide = showGuide;
        _scaleMode = scaleMode;
        _density = context.Resources?.DisplayMetrics?.Density ?? 1;
        _trackPathEffect = new DashPathEffect([6 * _density, 4 * _density], 0);
        _boxPaint.SetStyle(Paint.Style.Stroke);
        _labelPaint.SetStyle(Paint.Style.Fill);
        _textPaint.SetStyle(Paint.Style.Fill);
        _textPaint.SetTypeface(Typeface.DefaultBold);
        _detailPaint.SetStyle(Paint.Style.Fill);
        _detailPaint.SetTypeface(Typeface.Default);
        SetWillNotDraw(false);
        ImportantForAccessibility = ImportantForAccessibility.No;
    }

    public void Update(DriveSnapshot snapshot)
    {
        _snapshot = snapshot;
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var snapshot = _snapshot;
        if (snapshot is null || !snapshot.IsDriving)
        {
            return;
        }

        if (_showGuide())
        {
            DrawGuide(canvas);
        }

        foreach (var overlay in DriveOverlayLayout.GetVisibleOverlays(snapshot))
        {
            DrawDetection(canvas, overlay);
        }
    }

    private void DrawGuide(Canvas canvas)
    {
        var left = 0f;
        var top = Height * .18f;
        var right = Width;
        var bottom = Height * .94f;
        var length = Math.Min(42 * _density, Math.Min(right - left, bottom - top) / 5);
        _boxPaint.Color = AColor.Argb(165, 255, 255, 255);
        _boxPaint.StrokeWidth = 2 * _density;
        foreach (var line in new[]
        {
            (left, top, left + length, top), (left, top, left, top + length),
            (right, top, right - length, top), (right, top, right, top + length),
            (left, bottom, left + length, bottom), (left, bottom, left, bottom - length),
            (right, bottom, right - length, bottom), (right, bottom, right, bottom - length)
        })
        {
            canvas.DrawLine(line.Item1, line.Item2, line.Item3, line.Item4, _boxPaint);
        }
    }

    private void DrawDetection(Canvas canvas, DriveOverlay overlay)
    {
        if (!DriveOverlayLayout.TryProject(
                overlay,
                Width,
                Height,
                _scaleMode(),
                out var projected))
        {
            return;
        }
        var bounds = new ARectF(
            projected.Left,
            projected.Top,
            projected.Right,
            projected.Bottom);
        var confirmed = overlay.Kind == DriveOverlayKind.Confirmed;
        var accent = overlay.Kind == DriveOverlayKind.Track
            ? AColor.Rgb(215, 123, 255)
            : confirmed ? AColor.Rgb(245, 197, 66) : AColor.Rgb(88, 224, 194);
        _boxPaint.Color = accent;
        _boxPaint.StrokeWidth = (confirmed ? 3.5f : 2.25f) * _density;
        _boxPaint.SetPathEffect(overlay.Kind == DriveOverlayKind.Track ? _trackPathEffect : null);
        canvas.DrawRoundRect(bounds, 8 * _density, 8 * _density, _boxPaint);
        _boxPaint.SetPathEffect(null);

        _textPaint.TextSize = 14 * _density;
        _detailPaint.TextSize = 11 * _density;
        var horizontalPadding = 10 * _density;
        var labelWidth = Math.Min(
            Width - 16 * _density,
            Math.Max(
                bounds.Width(),
                Math.Max(_textPaint.MeasureText(overlay.Title), _detailPaint.MeasureText(overlay.Detail))
                + horizontalPadding * 2));
        var labelHeight = 48 * _density;
        var labelLeft = Math.Clamp(bounds.Left, 8 * _density, Width - labelWidth - 8 * _density);
        var labelTop = bounds.Top - labelHeight - 5 * _density;
        if (labelTop < 8 * _density)
        {
            labelTop = bounds.Bottom + 5 * _density;
        }

        var label = new ARectF(labelLeft, labelTop, labelLeft + labelWidth, labelTop + labelHeight);
        _labelPaint.Color = confirmed ? AColor.Argb(242, 245, 197, 66) : AColor.Argb(232, 11, 13, 16);
        canvas.DrawRoundRect(label, 10 * _density, 10 * _density, _labelPaint);
        _textPaint.Color = confirmed ? AColor.Rgb(20, 17, 5) : AColor.White;
        _detailPaint.Color = confirmed ? AColor.Rgb(45, 38, 10) : AColor.Rgb(190, 205, 214);
        canvas.DrawText(
            Ellipsize(overlay.Title, _textPaint, labelWidth - horizontalPadding * 2),
            labelLeft + horizontalPadding,
            labelTop + 19 * _density,
            _textPaint);
        canvas.DrawText(
            Ellipsize(overlay.Detail, _detailPaint, labelWidth - horizontalPadding * 2),
            labelLeft + horizontalPadding,
            labelTop + 38 * _density,
            _detailPaint);
    }

    private static string Ellipsize(string value, Paint paint, float width)
    {
        if (paint.MeasureText(value) <= width)
        {
            return value;
        }

        const string suffix = "…";
        while (value.Length > 1 && paint.MeasureText(value + suffix) > width)
        {
            value = value[..^1];
        }

        return value + suffix;
    }
}
