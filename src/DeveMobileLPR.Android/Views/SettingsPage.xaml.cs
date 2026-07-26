using DeveMobileLPR.AndroidApp.ViewModels;

namespace DeveMobileLPR.AndroidApp.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _viewModel;

    internal SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.RefreshAsync();
    }

    private async void ImportRdwClicked(object? sender, EventArgs args)
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Select rdw.sqlite" });
        if (file is not null) await _viewModel.ImportRdwAsync(file);
    }

    private static void OpenPermissionsClicked(object? sender, EventArgs args) => AppInfo.Current.ShowSettingsUI();

    private async void ExportClicked(object? sender, EventArgs args)
    {
        var path = await _viewModel.CreateExportAsync();
        await Share.Default.RequestAsync(new ShareFileRequest("RoadLens history export", new ShareFile(path)));
    }

    private async void DeleteClicked(object? sender, EventArgs args)
    {
        var confirmed = await DisplayAlertAsync("Delete all history?", "This permanently deletes every trip, route point, and sighting. The RDW vehicle database and preferences are kept.", "Delete", "Cancel");
        if (confirmed) await _viewModel.DeleteHistoryAsync();
    }
}
