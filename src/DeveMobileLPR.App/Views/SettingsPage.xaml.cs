using DeveMobileLPR.App.ViewModels;

namespace DeveMobileLPR.App.Views;

public partial class SettingsPage : ContentPage
{
    private const double PairedCardMinimumWidth = 656;
    private const double RdwSideBySideMinimumWidth = 466;
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
        await Share.Default.RequestAsync(new ShareFileRequest("DeveMobileLPR history export", new ShareFile(path)));
    }

    private async void DeleteClicked(object? sender, EventArgs args)
    {
        var confirmed = await DisplayAlertAsync("Delete all history?", "This permanently deletes every trip, route point, and sighting. The RDW vehicle database and preferences are kept.", "Delete", "Cancel");
        if (confirmed) await _viewModel.DeleteHistoryAsync();
    }

    private static void ResponsiveCardPairSizeChanged(object? sender, EventArgs args)
    {
        if (sender is not FlexLayout { Children.Count: > 0 } layout)
        {
            return;
        }

        var rightGap = layout.Width >= PairedCardMinimumWidth ? 16 : 0;
        if (layout.Children[0] is View firstCard)
        {
            var margin = new Thickness(0, 0, rightGap, 16);
            if (firstCard.Margin != margin)
            {
                firstCard.Margin = margin;
            }
        }
    }

    private static void ResponsiveRdwLayoutSizeChanged(object? sender, EventArgs args)
    {
        if (sender is not FlexLayout { Children.Count: > 0 } layout)
        {
            return;
        }

        var rightGap = layout.Width >= RdwSideBySideMinimumWidth ? 16 : 0;
        if (layout.Children[0] is View rdwSummary)
        {
            var margin = new Thickness(0, 0, rightGap, 12);
            if (rdwSummary.Margin != margin)
            {
                rdwSummary.Margin = margin;
            }
        }
    }
}
