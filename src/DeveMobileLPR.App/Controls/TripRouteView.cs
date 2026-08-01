using DeveMobileLPR.Recognition;
using Microsoft.Maui.Graphics;

namespace DeveMobileLPR.App.Controls;

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

    protected override void OnParentSet()
    {
        if (Parent is null && Microsoft.Maui.Controls.Application.Current is { } oldApplication)
        {
            oldApplication.RequestedThemeChanged -= ThemeChanged;
        }
        base.OnParentSet();
        if (Parent is not null && Microsoft.Maui.Controls.Application.Current is { } application)
        {
            application.RequestedThemeChanged -= ThemeChanged;
            application.RequestedThemeChanged += ThemeChanged;
        }
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

    private void ThemeChanged(object? sender, AppThemeChangedEventArgs args) => Dispatcher.Dispatch(Invalidate);

    private sealed class RouteDrawable : IDrawable
    {
        public IReadOnlyList<TripPoint> Points { get; set; } = [];

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.FillColor = ResolveColor("SurfaceRaised", "#202632");
            canvas.FillRoundedRectangle(dirtyRect, 20);
            if (Points.Count == 0)
            {
                canvas.FontColor = ResolveColor("TextMuted", "#747E8E");
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
            canvas.StrokeColor = ResolveColor("Primary", "#58E0C2");
            canvas.StrokeSize = 4;
            canvas.StrokeLineCap = LineCap.Round;
            canvas.DrawPath(path);
            var last = Map(Points[^1]);
            canvas.FillColor = ResolveColor("PlateYellow", "#F5C542");
            canvas.FillCircle(first, 7);
            canvas.FillColor = ResolveColor("Primary", "#58E0C2");
            canvas.FillCircle(last, 8);
            canvas.StrokeColor = ResolveColor("Surface", "#151922");
            canvas.StrokeSize = 2;
            canvas.DrawCircle(last, 8);
        }

        private static Color ResolveColor(string key, string fallback) =>
            Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color
                ? color
                : Color.FromArgb(fallback);
    }
}
