namespace DeveMobileLPR.App.Controls;

internal sealed class TripCardView : Border
{
    public static readonly BindableProperty IsSelectionModeProperty = BindableProperty.Create(
        nameof(IsSelectionMode),
        typeof(bool),
        typeof(TripCardView),
        false,
        propertyChanged: SelectionModeChanged);

    private bool _usesNativeGestures;

    public event EventHandler? NativeTapped;
    public event EventHandler? NativeLongPressed;

    public bool IsSelectionMode
    {
        get => (bool)GetValue(IsSelectionModeProperty);
        set => SetValue(IsSelectionModeProperty, value);
    }

    public bool ShowSelectionCheckbox => !_usesNativeGestures || IsSelectionMode;

    internal void SetUsesNativeGestures(bool value)
    {
        if (_usesNativeGestures == value)
        {
            return;
        }

        _usesNativeGestures = value;
        OnPropertyChanged(nameof(ShowSelectionCheckbox));
    }

    internal void SendNativeTapped() => NativeTapped?.Invoke(this, EventArgs.Empty);
    internal void SendNativeLongPressed() => NativeLongPressed?.Invoke(this, EventArgs.Empty);

    private static void SelectionModeChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((TripCardView)bindable).OnPropertyChanged(nameof(ShowSelectionCheckbox));
}
