namespace DeveMobileLPR.App.Controls;

public partial class DriveInputSelector : ContentView
{
    public DriveInputSelector() => InitializeComponent();

    private async void MultiModeToggled(object? sender, ToggledEventArgs args)
    {
        if (!args.Value)
        {
            return;
        }

        MultiPanel.Opacity = 0.35;
        MultiPanel.Scale = 0.98;
        await Task.WhenAll(
            MultiPanel.FadeToAsync(1, 180, Easing.CubicOut),
            MultiPanel.ScaleToAsync(1, 180, Easing.CubicOut));
    }
}
