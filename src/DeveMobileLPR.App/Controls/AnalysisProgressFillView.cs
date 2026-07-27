namespace DeveMobileLPR.App.Controls;

internal sealed class AnalysisProgressFillView : GraphicsView, IDrawable
{
    public static readonly BindableProperty ProgressProperty = BindableProperty.Create(
        nameof(Progress),
        typeof(double),
        typeof(AnalysisProgressFillView),
        0d,
        propertyChanged: static (bindable, _, _) => ((AnalysisProgressFillView)bindable).Invalidate());

    public static readonly BindableProperty FillColorProperty = BindableProperty.Create(
        nameof(FillColor),
        typeof(Color),
        typeof(AnalysisProgressFillView),
        Colors.Transparent,
        propertyChanged: static (bindable, _, _) => ((AnalysisProgressFillView)bindable).Invalidate());

    public AnalysisProgressFillView()
    {
        Drawable = this;
        InputTransparent = true;
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public Color FillColor
    {
        get => (Color)GetValue(FillColorProperty);
        set => SetValue(FillColorProperty, value);
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var width = dirtyRect.Width * (float)Math.Clamp(Progress, 0, 1);
        if (width <= 0)
        {
            return;
        }

        canvas.FillColor = FillColor;
        canvas.FillRoundedRectangle(dirtyRect.Left, dirtyRect.Top, width, dirtyRect.Height, 6);
    }
}