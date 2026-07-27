using DeveMobileLPR.AndroidApp.ViewModels;

namespace DeveMobileLPR.AndroidApp.Views;

public partial class AnalyzePage : ContentPage
{
    private static readonly FilePickerFileType VideoFiles = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.Android] = ["video/*"]
    });
    private readonly AnalyzeViewModel _viewModel;

    internal AnalyzePage(AnalyzeViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
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
}