using Android.Content;
using Android.Graphics;
using Android.Views;
using DeveMobileLPR.Geometry;

namespace DeveMobileLPR.AndroidApp.UI;

internal sealed class RoadGuideView : View
{
    private readonly Paint _border = new()
    {
        Color = Color.Rgb(255, 193, 7),
        StrokeWidth = 4,
        AntiAlias = true
    };

    public RoadGuideView(Context context) : base(context)
    {
        _border.SetStyle(Paint.Style.Stroke);
        SetBackgroundColor(Color.Transparent);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var region = NormalizedRegion.RoadDefault;
        canvas.DrawRect(region.Left * Width, region.Top * Height, region.Right * Width, region.Bottom * Height, _border);
    }
}
