namespace DeveMobileLPR.App.Controls;

using DeveMobileLPR.App.ViewModels;

public partial class DriveInputSelector : ContentView
{
    private bool _singleZoomDragging;
    private readonly HashSet<Slider> _draggingCropSliders = [];

    public DriveInputSelector() => InitializeComponent();

    private void SingleZoomLoaded(object? sender, EventArgs args) =>
        SynchronizeSingleZoomThumb();

    private void SingleSourceChanged(object? sender, EventArgs args) =>
        SynchronizeSingleZoomThumb();

    private void SynchronizeSingleZoomThumb()
    {
        Dispatcher.Dispatch(() =>
        {
            if (BindingContext is DriveViewModel { SelectedSingleSource: { } source })
            {
                // Minimum and Maximum must reach the native SeekBar before Value. Assigning the
                // persisted value on the next UI turn prevents Android's initial coercion to 1×
                // from leaving the thumb visually out of sync with the model and label.
                SingleZoomSlider.Value = Math.Clamp(
                    source.Zoom,
                    source.MinimumZoom,
                    source.MaximumZoom);
            }
        });
    }

    private void SingleZoomChanged(object? sender, ValueChangedEventArgs args)
    {
        if (_singleZoomDragging && BindingContext is DriveViewModel viewModel)
        {
            viewModel.PreviewSingleZoom(args.NewValue);
        }
    }

    private void SingleZoomDragStarted(object? sender, EventArgs args) =>
        _singleZoomDragging = true;

    private void SingleZoomDragCompleted(object? sender, EventArgs args)
    {
        try
        {
            if (sender is Slider slider
                && BindingContext is DriveViewModel viewModel)
            {
                viewModel.CommitSingleZoom(slider.Value);
            }
        }
        finally
        {
            _singleZoomDragging = false;
        }
    }

    private void MultiCropDragStarted(object? sender, EventArgs args)
    {
        if (sender is Slider slider)
        {
            _draggingCropSliders.Add(slider);
        }
    }

    private void MultiCropChanged(object? sender, ValueChangedEventArgs args)
    {
        if (sender is Slider slider
            && _draggingCropSliders.Contains(slider)
            && slider.BindingContext is DriveSourceOptionViewModel source)
        {
            source.Crop = args.NewValue;
        }
    }

    private void MultiCropDragCompleted(object? sender, EventArgs args)
    {
        if (sender is not Slider slider)
        {
            return;
        }

        try
        {
            if (slider.BindingContext is DriveSourceOptionViewModel source)
            {
                source.Crop = slider.Value;
            }
        }
        finally
        {
            _draggingCropSliders.Remove(slider);
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
