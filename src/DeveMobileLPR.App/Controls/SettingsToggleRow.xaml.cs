namespace DeveMobileLPR.App.Controls;

public partial class SettingsToggleRow : ContentView
{
    public event EventHandler<ToggledEventArgs>? Toggled;

    public static readonly BindableProperty TitleProperty = BindableProperty.Create(
        nameof(Title), typeof(string), typeof(SettingsToggleRow), string.Empty);

    public static readonly BindableProperty DescriptionProperty = BindableProperty.Create(
        nameof(Description), typeof(string), typeof(SettingsToggleRow), string.Empty);

    public static readonly BindableProperty IsToggledProperty = BindableProperty.Create(
        nameof(IsToggled), typeof(bool), typeof(SettingsToggleRow), false, BindingMode.TwoWay);

    public SettingsToggleRow() => InitializeComponent();

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public bool IsToggled
    {
        get => (bool)GetValue(IsToggledProperty);
        set => SetValue(IsToggledProperty, value);
    }

    private void SwitchToggled(object? sender, ToggledEventArgs args) => Toggled?.Invoke(this, args);
}
