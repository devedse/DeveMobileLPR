using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.Camera.View;
using AndroidX.Lifecycle;
using DeveMobileLPR.AndroidApp.Camera;
using DeveMobileLPR.AndroidApp.Controls;
using DeveMobileLPR.AndroidApp.Services;
using DeveMobileLPR.Geometry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;
using AColor = Android.Graphics.Color;
using ARectF = Android.Graphics.RectF;
using AView = Android.Views.View;
using Paint = Android.Graphics.Paint;

namespace DeveMobileLPR.AndroidApp;

internal sealed class CameraPreviewHandler : ViewHandler<CameraPreview, FrameLayout>
{
    public static readonly IPropertyMapper<CameraPreview, CameraPreviewHandler> Mapper =
        new PropertyMapper<CameraPreview, CameraPreviewHandler>(ViewHandler.ViewMapper);

    private CameraXFrameSource? _source;
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
        preview.SetScaleType(PreviewView.ScaleType.FillCenter);
        // Compatible uses TextureView, which guarantees the custom detection layer composites above it.
        preview.SetImplementationMode(PreviewView.ImplementationMode.Compatible);
        root.AddView(preview, match);
        _overlay = new DetectionOverlayView(context, () => settings.ShowRoadGuide);
        root.AddView(_overlay, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _source = new CameraXFrameSource(context, activity, preview, frame => _coordinator.SubmitFrame(frame));
        _coordinator.AttachCamera(_source);
        _coordinator.SnapshotChanged += SnapshotChanged;
        _overlay.Update(_coordinator.Snapshot);
        return root;
    }

    private void SnapshotChanged(object? sender, DriveSnapshot snapshot) => _overlay?.Update(snapshot);

    protected override void DisconnectHandler(FrameLayout platformView)
    {
        if (_coordinator is not null)
        {
            _coordinator.SnapshotChanged -= SnapshotChanged;
        }
        if (_source is not null)
        {
            _coordinator?.DetachCamera(_source);
            _source.Dispose();
            _source = null;
        }
        _overlay = null;
        _coordinator = null;
        base.DisconnectHandler(platformView);
    }
}

internal sealed class DetectionOverlayView : AView
{
    private readonly Paint _boxPaint = new(PaintFlags.AntiAlias);
    private readonly Paint _labelPaint = new(PaintFlags.AntiAlias);
    private readonly Paint _textPaint = new(PaintFlags.AntiAlias);
    private readonly Paint _detailPaint = new(PaintFlags.AntiAlias);
    private readonly Func<bool> _showGuide;
    private DriveSnapshot? _snapshot;
    private readonly float _density;

    public DetectionOverlayView(Context context, Func<bool> showGuide) : base(context)
    {
        _showGuide = showGuide;
        _density = context.Resources?.DisplayMetrics?.Density ?? 1;
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

        foreach (var overlay in snapshot.Overlays.OrderBy(static item => item.Confirmed))
        {
            DrawDetection(canvas, overlay);
        }
    }

    private void DrawGuide(Canvas canvas)
    {
        var left = Width * .03f;
        var top = Height * .18f;
        var right = Width * .97f;
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
        if (overlay.SourceWidth <= 1 || overlay.SourceHeight <= 1)
        {
            return;
        }

        var scale = Math.Max(Width / (float)overlay.SourceWidth, Height / (float)overlay.SourceHeight);
        var offsetX = (Width - overlay.SourceWidth * scale) / 2f;
        var offsetY = (Height - overlay.SourceHeight * scale) / 2f;
        var bounds = new ARectF(
            overlay.Bounds.Left * scale + offsetX,
            overlay.Bounds.Top * scale + offsetY,
            overlay.Bounds.Right * scale + offsetX,
            overlay.Bounds.Bottom * scale + offsetY);
        var accent = overlay.Confirmed ? AColor.Rgb(245, 197, 66) : AColor.Rgb(88, 224, 194);
        _boxPaint.Color = accent;
        _boxPaint.StrokeWidth = (overlay.Confirmed ? 3.5f : 2.25f) * _density;
        canvas.DrawRoundRect(bounds, 8 * _density, 8 * _density, _boxPaint);

        _textPaint.TextSize = 14 * _density;
        _detailPaint.TextSize = 11 * _density;
        var horizontalPadding = 10 * _density;
        var labelWidth = Math.Min(Width - 16 * _density, Math.Max(bounds.Width(), Math.Max(_textPaint.MeasureText(overlay.Title), _detailPaint.MeasureText(overlay.Detail)) + horizontalPadding * 2));
        var labelHeight = 48 * _density;
        var labelLeft = Math.Clamp(bounds.Left, 8 * _density, Width - labelWidth - 8 * _density);
        var labelTop = bounds.Top - labelHeight - 5 * _density;
        if (labelTop < 8 * _density) labelTop = bounds.Bottom + 5 * _density;
        var label = new ARectF(labelLeft, labelTop, labelLeft + labelWidth, labelTop + labelHeight);
        _labelPaint.Color = overlay.Confirmed ? AColor.Argb(242, 245, 197, 66) : AColor.Argb(232, 11, 13, 16);
        canvas.DrawRoundRect(label, 10 * _density, 10 * _density, _labelPaint);
        _textPaint.Color = overlay.Confirmed ? AColor.Rgb(20, 17, 5) : AColor.White;
        _detailPaint.Color = overlay.Confirmed ? AColor.Rgb(45, 38, 10) : AColor.Rgb(190, 205, 214);
        canvas.DrawText(Ellipsize(overlay.Title, _textPaint, labelWidth - horizontalPadding * 2), labelLeft + horizontalPadding, labelTop + 19 * _density, _textPaint);
        canvas.DrawText(Ellipsize(overlay.Detail, _detailPaint, labelWidth - horizontalPadding * 2), labelLeft + horizontalPadding, labelTop + 38 * _density, _detailPaint);
    }

    private static string Ellipsize(string value, Paint paint, float width)
    {
        if (paint.MeasureText(value) <= width) return value;
        const string suffix = "…";
        while (value.Length > 1 && paint.MeasureText(value + suffix) > width) value = value[..^1];
        return value + suffix;
    }
}
