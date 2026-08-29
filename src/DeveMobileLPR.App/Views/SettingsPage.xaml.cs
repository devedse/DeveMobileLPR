using DeveMobileLPR.App.ViewModels;

namespace DeveMobileLPR.App.Views;

public partial class SettingsPage : ContentPage
{
    private const double PairedCardMinimumWidth = 656;
    private const double RdwSideBySideMinimumWidth = 466;
    private readonly SettingsViewModel _viewModel;
    private readonly Services.AppLogService _appLog;

    internal SettingsPage(SettingsViewModel viewModel, Services.AppLogService appLog)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _appLog = appLog;
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

    private void OpenPermissionsClicked(object? sender, EventArgs args) => _viewModel.OpenAppSettings();

    private async void OpenAppLogsClicked(object? sender, EventArgs args) =>
        await Navigation.PushModalAsync(new NavigationPage(new AppLogsPage(_appLog)));

    private async void SeedDebugHistoryClicked(object? sender, EventArgs args)
    {
#if DEBUG
        await _viewModel.SeedDebugHistoryAsync();
#else
        await Task.CompletedTask;
#endif
    }

    private async void CameraCapabilitiesClicked(object? sender, EventArgs args)
    {
#if ANDROID
        await Navigation.PushModalAsync(new NavigationPage(
            new Platforms.Android.Camera.CameraCapabilitiesPage()));
#else
        await Task.CompletedTask;
#endif
    }

    private async void BackgroundScanningToggled(object? sender, ToggledEventArgs args)
    {
        if (args.Value && !await _viewModel.PrepareBackgroundScanningAsync())
        {
            await DisplayAlertAsync(
                "Camera permission required",
                "Background recognition needs camera access. The setting has remained off.",
                "OK");
        }
    }

    private async void ExportClicked(object? sender, EventArgs args)
    {
        var path = await _viewModel.CreateExportAsync();
        await Share.Default.RequestAsync(new ShareFileRequest("DeveMobileLPR history export", new ShareFile(path)));
    }

    private async void BackupClicked(object? sender, EventArgs args)
    {
        var path = await _viewModel.CreateBackupAsync();
        if (path is not null)
        {
            await Share.Default.RequestAsync(new ShareFileRequest(
                "DeveMobileLPR history backup",
                new ShareFile(path)));
        }
    }

    private async void ImportBackupClicked(object? sender, EventArgs args)
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Select a DeveMobileLPR backup ZIP"
        });
        if (file is null)
        {
            return;
        }

        var manifest = await _viewModel.InspectBackupAsync(file);
        if (manifest is null)
        {
            return;
        }

        var confirmed = await DisplayAlertAsync(
            "Replace current history?",
            $"Backup from {manifest.CreatedAtUtc.ToLocalTime():g}\n" +
            $"App {manifest.AppVersion} ({manifest.AppBuild})\n\n" +
            $"{manifest.TripCount} trips · {manifest.SightingCount} sightings · " +
            $"{manifest.TripPointCount} route points · {manifest.VehicleSnapshotCount} screenshots\n\n" +
            "This replaces all current trip history and screenshots. RDW data and preferences are kept.",
            "Import",
            "Cancel");
        if (confirmed)
        {
            await _viewModel.ImportBackupAsync(file);
        }
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
