namespace DeveMobileLPR.App.Controls;

internal sealed class FrameSnapSlider : Slider
{
    public static readonly BindableProperty SnapPointsProperty = BindableProperty.Create(
        nameof(SnapPoints),
        typeof(IReadOnlyList<double>),
        typeof(FrameSnapSlider),
        Array.Empty<double>());

    private bool _isSnapping;

    public FrameSnapSlider()
    {
        ValueChanged += SnapValueChanged;
    }

    public IReadOnlyList<double> SnapPoints
    {
        get => (IReadOnlyList<double>)GetValue(SnapPointsProperty);
        set => SetValue(SnapPointsProperty, value);
    }

    private void SnapValueChanged(object? sender, ValueChangedEventArgs args)
    {
        if (_isSnapping || SnapPoints.Count == 0)
        {
            return;
        }

        var snapped = FindClosest(SnapPoints, args.NewValue);
        if (Math.Abs(snapped - args.NewValue) < 0.0000001)
        {
            return;
        }

        _isSnapping = true;
        SetValue(ValueProperty, snapped);
        _isSnapping = false;
    }

    private static double FindClosest(IReadOnlyList<double> points, double value)
    {
        var low = 0;
        var high = points.Count - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (points[middle] < value) low = middle + 1;
            else high = middle;
        }
        return low > 0 && value - points[low - 1] <= points[low] - value ? points[low - 1] : points[low];
    }
}