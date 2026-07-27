namespace DeveMobileLPR.App.Controls;

/// <summary>
/// A uniform card grid that chooses a column count from its available width.
/// It replaces per-item margins and page-specific wrap calculations.
/// </summary>
public sealed class ResponsiveGrid : Grid
{
    public static readonly BindableProperty MinimumItemWidthProperty = BindableProperty.Create(
        nameof(MinimumItemWidth), typeof(double), typeof(ResponsiveGrid), 280d,
        propertyChanged: LayoutPropertyChanged);

    public static readonly BindableProperty MaximumColumnsProperty = BindableProperty.Create(
        nameof(MaximumColumns), typeof(int), typeof(ResponsiveGrid), 2,
        validateValue: static (_, value) => (int)value >= 1,
        propertyChanged: LayoutPropertyChanged);

    private int _columns;
    private int _childCount;

    public ResponsiveGrid()
    {
        SizeChanged += (_, _) => UpdateGrid();
        ChildAdded += (_, _) => UpdateGrid();
        ChildRemoved += (_, _) => UpdateGrid();
        Loaded += (_, _) => UpdateGrid(force: true);
    }

    public double MinimumItemWidth
    {
        get => (double)GetValue(MinimumItemWidthProperty);
        set => SetValue(MinimumItemWidthProperty, value);
    }

    public int MaximumColumns
    {
        get => (int)GetValue(MaximumColumnsProperty);
        set => SetValue(MaximumColumnsProperty, value);
    }

    private static void LayoutPropertyChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((ResponsiveGrid)bindable).UpdateGrid(force: true);

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        UpdateGrid(width);
    }

    private void UpdateGrid(bool force = false) => UpdateGrid(Width, force);

    private void UpdateGrid(double availableWidth, bool force = false)
    {
        if (availableWidth <= 0 || Children.Count == 0)
        {
            return;
        }

        var columns = Math.Clamp(
            (int)Math.Floor((availableWidth + ColumnSpacing) / (Math.Max(1, MinimumItemWidth) + ColumnSpacing)),
            1,
            MaximumColumns);
        if (!force && columns == _columns && Children.Count == _childCount)
        {
            return;
        }

        _columns = columns;
        _childCount = Children.Count;
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        for (var column = 0; column < columns; column++)
        {
            ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        var rows = (Children.Count + columns - 1) / columns;
        for (var row = 0; row < rows; row++)
        {
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (var index = 0; index < Children.Count; index++)
        {
            var child = (BindableObject)Children[index];
            SetRow(child, index / columns);
            SetColumn(child, index % columns);
        }

        InvalidateMeasure();
    }
}
