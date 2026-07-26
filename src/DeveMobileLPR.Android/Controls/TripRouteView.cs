using DeveMobileLPR.Recognition;
using Microsoft.Maui.Graphics;

namespace DeveMobileLPR.AndroidApp.Controls;

internal sealed class TripRouteView : GraphicsView
{
    public static readonly BindableProperty PointsProperty = BindableProperty.Create(
        nameof(Points), typeof(IReadOnlyList<TripPoint>), typeof(TripRouteView), Array.Empty<TripPoint>(), propertyChanged: PointsChanged);

    private readonly RouteDrawable _drawable = new();

    public TripRouteView()
    {
        Drawable = _drawable;
        HeightRequest = 230;
    }

    public IReadOnlyList<TripPoint> Points
    {
        get => (IReadOnlyList<TripPoint>)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    private static void PointsChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (TripRouteView)bindable;
        view._drawable.Points = (IReadOnlyList<TripPoint>?)newValue ?? [];
        view.Invalidate();
    }

    private sealed class RouteDrawable : IDrawable
    {
        public IReadOnlyList<TripPoint> Points { get; set; } = [];

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.FillColor = Color.FromArgb("#10141A");
            canvas.FillRoundedRectangle(dirtyRect, 20);
            if (Points.Count == 0)
            {
                canvas.FontColor = Color.FromArgb("#747E8E");
                canvas.FontSize = 14;
                canvas.DrawString("No route points for this drive", dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center);
                return;
            }

            var minLat = Points.Min(point => point.Location.Latitude);
            var maxLat = Points.Max(point => point.Location.Latitude);
            var minLon = Points.Min(point => point.Location.Longitude);
            var maxLon = Points.Max(point => point.Location.Longitude);
            var latitudeRange = Math.Max(0.0001, maxLat - minLat);
            var longitudeRange = Math.Max(0.0001, maxLon - minLon);
            const float padding = 30;
            PointF Map(TripPoint point) => new(
                padding + (float)((point.Location.Longitude - minLon) / longitudeRange) * (dirtyRect.Width - padding * 2),
                dirtyRect.Height - padding - (float)((point.Location.Latitude - minLat) / latitudeRange) * (dirtyRect.Height - padding * 2));

            var path = new PathF();
            var first = Map(Points[0]);
            path.MoveTo(first.X, first.Y);
            foreach (var point in Points.Skip(1))
            {
                var mapped = Map(point);
                path.LineTo(mapped.X, mapped.Y);
            }
            canvas.StrokeColor = Color.FromArgb("#58E0C2");
            canvas.StrokeSize = 4;
            canvas.StrokeLineCap = LineCap.Round;
            canvas.DrawPath(path);
            var last = Map(Points[^1]);
            canvas.FillColor = Color.FromArgb("#F5C542");
            canvas.FillCircle(first, 7);
            canvas.FillColor = Color.FromArgb("#58E0C2");
            canvas.FillCircle(last, 8);
            canvas.StrokeColor = Colors.White;
            canvas.StrokeSize = 2;
            canvas.DrawCircle(last, 8);
        }
    }
}
