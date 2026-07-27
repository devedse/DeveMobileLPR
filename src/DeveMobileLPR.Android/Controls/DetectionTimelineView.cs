namespace DeveMobileLPR.AndroidApp.Controls;

internal sealed class DetectionTimelineView : GraphicsView, IDrawable
{
    public static readonly BindableProperty MarkersProperty = BindableProperty.Create(
        nameof(Markers),
        typeof(IReadOnlyList<double>),
        typeof(DetectionTimelineView),
        Array.Empty<double>(),
        propertyChanged: static (bindable, _, _) => ((DetectionTimelineView)bindable).Invalidate());

    public DetectionTimelineView()
    {
        Drawable = this;
        HeightRequest = 30;
        InputTransparent = true;
    }

    public IReadOnlyList<double> Markers
    {
        get => (IReadOnlyList<double>)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var centerY = dirtyRect.Center.Y;
        canvas.StrokeColor = Color.FromArgb("#68707A");
        canvas.StrokeSize = 2;
        canvas.DrawLine(dirtyRect.Left, centerY, dirtyRect.Right, centerY);

        canvas.FillColor = Color.FromArgb("#F6C945");
        foreach (var marker in Markers)
        {
            var x = dirtyRect.Left + dirtyRect.Width * (float)Math.Clamp(marker, 0, 1);
            canvas.FillRoundedRectangle(x - 2, centerY - 10, 4, 20, 2);
        }
    }
}
