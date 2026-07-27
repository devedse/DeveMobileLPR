using DeveMobileLPR.AndroidApp.ViewModels;

namespace DeveMobileLPR.AndroidApp.Views;

public partial class AnalyzePage : ContentPage
{
    private static readonly FilePickerFileType VideoFiles = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.Android] = ["video/*"],
        [DevicePlatform.WinUI] = [".mp4", ".mov", ".m4v", ".avi", ".wmv", ".mkv"]
    });
    private readonly AnalyzeViewModel _viewModel;

    internal AnalyzePage(AnalyzeViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }

    private async void SelectVideoClicked(object? sender, EventArgs args)
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Select a video to analyze",
            FileTypes = VideoFiles
        });
        if (file is not null)
        {
            await _viewModel.SelectFileAsync(file);
        }
    }

    private void CloseReviewClicked(object? sender, EventArgs args) => _viewModel.CloseReview();

    private async void TimelineDragCompleted(object? sender, EventArgs args)
    {
        if (sender is Slider slider)
        {
            await _viewModel.SeekToFractionAsync(slider.Value);
        }
    }
}