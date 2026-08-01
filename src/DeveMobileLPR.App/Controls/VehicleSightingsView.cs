using DeveMobileLPR.Recognition;
using Microsoft.Maui.Graphics;

namespace DeveMobileLPR.App.Controls;

internal sealed class VehicleSightingsView : GraphicsView
{
    public static readonly BindableProperty SightingsProperty = BindableProperty.Create(
        nameof(Sightings),
        typeof(IReadOnlyList<Sighting>),
        typeof(VehicleSightingsView),
        Array.Empty<Sighting>(),
        propertyChanged: SightingsChanged);

    private readonly SightingsDrawable _drawable = new();

    public VehicleSightingsView()
    {
        Drawable = _drawable;
        HeightRequest = 220;
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

    public IReadOnlyList<Sighting> Sightings
    {
        get => (IReadOnlyList<Sighting>)GetValue(SightingsProperty);
        set => SetValue(SightingsProperty, value);
    }

    private static void SightingsChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (VehicleSightingsView)bindable;
        view._drawable.Sightings = (IReadOnlyList<Sighting>?)newValue ?? [];
        view.Invalidate();
    }

    private void ThemeChanged(object? sender, AppThemeChangedEventArgs args) => Dispatcher.Dispatch(Invalidate);

    private sealed class SightingsDrawable : IDrawable
    {
        public IReadOnlyList<Sighting> Sightings { get; set; } = [];

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.FillColor = ResolveColor("SurfaceRaised", "#202632");
            canvas.FillRoundedRectangle(dirtyRect, 18);
            var located = Sightings.Where(sighting => sighting.Location is not null).OrderBy(sighting => sighting.FirstSeenAt).ToArray();
            if (located.Length == 0)
            {
                canvas.FontColor = ResolveColor("TextMuted", "#747E8E");
                canvas.FontSize = 14;
                canvas.DrawString("No locations recorded for this vehicle", dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center);
                return;
            }

            const float padding = 34;
            var minLat = located.Min(sighting => sighting.Location!.Value.Latitude);
            var maxLat = located.Max(sighting => sighting.Location!.Value.Latitude);
            var minLon = located.Min(sighting => sighting.Location!.Value.Longitude);
            var maxLon = located.Max(sighting => sighting.Location!.Value.Longitude);
            var latitudeRange = Math.Max(0.0001, maxLat - minLat);
            var longitudeRange = Math.Max(0.0001, maxLon - minLon);
            PointF Map(Sighting sighting) => new(
                padding + (float)((sighting.Location!.Value.Longitude - minLon) / longitudeRange) * (dirtyRect.Width - padding * 2),
                dirtyRect.Height - padding - (float)((sighting.Location!.Value.Latitude - minLat) / latitudeRange) * (dirtyRect.Height - padding * 2));

            if (located.Length > 1)
            {
                var path = new PathF();
                var firstPoint = Map(located[0]);
                path.MoveTo(firstPoint.X, firstPoint.Y);
                foreach (var sighting in located.Skip(1))
                {
                    var point = Map(sighting);
                    path.LineTo(point.X, point.Y);
                }

                canvas.StrokeColor = ResolveColor("Divider", "#303744");
                canvas.StrokeSize = 3;
                canvas.StrokeLineCap = LineCap.Round;
                canvas.DrawPath(path);
            }

            var markerGroups = located
                .GroupBy(LocationKey)
                .Select(group => new
                {
                    Sighting = group.MaxBy(sighting => sighting.FirstSeenAt)!,
                    Count = group.Count(),
                    IsLatest = group.Any(sighting => sighting.Id == located[^1].Id)
                })
                .OrderBy(group => group.Sighting.FirstSeenAt);
            foreach (var markerGroup in markerGroups)
            {
                var point = Map(markerGroup.Sighting);
                var radius = markerGroup.Count > 1 ? 12 : markerGroup.IsLatest ? 9 : 7;
                canvas.FillColor = ResolveColor(markerGroup.IsLatest ? "Primary" : "PlateYellow", markerGroup.IsLatest ? "#58E0C2" : "#F5C542");
                canvas.FillCircle(point, radius);
                canvas.StrokeColor = ResolveColor("Surface", "#151922");
                canvas.StrokeSize = 2;
                canvas.DrawCircle(point, radius);
                if (markerGroup.Count > 1)
                {
                    canvas.FontColor = ResolveColor(markerGroup.IsLatest ? "OnPrimary" : "OnPlate", markerGroup.IsLatest ? "#07110F" : "#111318");
                    canvas.FontSize = 11;
                    canvas.Font = null;
                    canvas.DrawString(markerGroup.Count.ToString(), new RectF(point.X - radius, point.Y - radius, radius * 2, radius * 2), HorizontalAlignment.Center, VerticalAlignment.Center);
                }
            }
        }

        private static (double Latitude, double Longitude) LocationKey(Sighting sighting) =>
            (Math.Round(sighting.Location!.Value.Latitude, 5), Math.Round(sighting.Location!.Value.Longitude, 5));

        private static Color ResolveColor(string key, string fallback) =>
            Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color
                ? color
                : Color.FromArgb(fallback);
    }
}
