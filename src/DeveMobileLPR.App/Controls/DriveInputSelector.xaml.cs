namespace DeveMobileLPR.App.Controls;

using DeveMobileLPR.App.ViewModels;

public partial class DriveInputSelector : ContentView
{
    public DriveInputSelector() => InitializeComponent();

    private void SingleZoomChanged(object? sender, ValueChangedEventArgs args)
    {
        if (BindingContext is DriveViewModel viewModel)
        {
            viewModel.PreviewSingleZoom(args.NewValue);
        }
    }

    private void SingleZoomDragCompleted(object? sender, EventArgs args)
    {
        if (sender is Slider slider
            && BindingContext is DriveViewModel viewModel)
        {
            viewModel.CommitSingleZoom(slider.Value);
        }
    }

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
