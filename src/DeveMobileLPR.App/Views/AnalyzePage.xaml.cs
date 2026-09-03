using DeveMobileLPR.App.Services;
using DeveMobileLPR.App.ViewModels;

namespace DeveMobileLPR.App.Views;

public partial class AnalyzePage : ContentPage
{
    private readonly AnalyzeViewModel _viewModel;

    internal AnalyzePage(AnalyzeViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.RefreshSettings();
        await this.RunSafelyAsync(
            "Could not load analyses",
            _viewModel.InitializeAsync);
    }

    private async void SelectVideoClicked(object? sender, EventArgs args) =>
        await this.RunSafelyAsync(
            "Could not select video",
            async () =>
            {
                var file = await FilePicker.Default.PickAsync(new PickOptions
                {
                    PickerTitle = "Select a video to analyze",
                    FileTypes = FilePickerFileType.Videos
                });
                if (file is not null)
                {
                    await _viewModel.SelectFileAsync(file);
                }
            });

    private async void TimelineDragCompleted(object? sender, EventArgs args)
    {
        if (sender is Slider slider)
        {
            await this.RunSafelyAsync(
                "Could not seek video",
                () => _viewModel.SeekToFractionAsync(slider.Value));
        }
    }
}
