namespace DeveMobileLPR.App.Controls;

/// <summary>
/// Keeps four dashboard metrics edge-aligned with surrounding content while using
/// two columns on phones and four columns when enough width is available.
/// </summary>
public sealed class ResponsiveMetricGrid : Grid
{
    private const double FourColumnMinimumWidth = 640;
    private int _columnCount;

    public ResponsiveMetricGrid()
    {
        ColumnSpacing = 12;
        RowSpacing = 12;
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);

        var columnCount = width >= FourColumnMinimumWidth ? 4 : 2;
        if (_columnCount == columnCount || Children.Count == 0)
        {
            return;
        }

        _columnCount = columnCount;
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();

        for (var column = 0; column < columnCount; column++)
        {
            ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        var rowCount = (int)Math.Ceiling(Children.Count / (double)columnCount);
        for (var row = 0; row < rowCount; row++)
        {
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (var index = 0; index < Children.Count; index++)
        {
            SetColumn((BindableObject)Children[index], index % columnCount);
            SetRow((BindableObject)Children[index], index / columnCount);
        }

        InvalidateMeasure();
    }
}
