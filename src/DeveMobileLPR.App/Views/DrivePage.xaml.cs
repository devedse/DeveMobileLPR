using DeveMobileLPR.App.Services;
using DeveMobileLPR.App.ViewModels;

namespace DeveMobileLPR.App.Views;

public partial class DrivePage : ContentPage
{
    private readonly DriveViewModel _viewModel;
    private readonly IDriveDisplayMode _displayMode;

    internal DrivePage(DriveViewModel viewModel, IDriveDisplayMode displayMode)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _displayMode = displayMode;
        _viewModel.DriveModeChanged += DriveModeChanged;
        SizeChanged += PageSizeChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ApplyDriveMode(_viewModel.IsDriving);
        await _viewModel.InitializeAsync();
    }

    private void DriveModeChanged(object? sender, bool isDriving) => ApplyDriveMode(isDriving);

    private void PageSizeChanged(object? sender, EventArgs args)
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        var compactLandscape = Width > Height;
        ReadyPortraitContent.IsVisible = !compactLandscape;
        ReadyLandscapeContent.IsVisible = compactLandscape;
        ReadyPanel.MaximumWidthRequest = compactLandscape ? 900 : 570;
        ReadyPanel.MaximumHeightRequest = Math.Max(320, Height - 120);
        ReadyPanel.Padding = compactLandscape ? new Thickness(24, 16) : new Thickness(30);
        ReadyPanel.VerticalOptions = LayoutOptions.Center;
        ReadyPanel.Margin = compactLandscape ? new Thickness(26, 4) : Thickness.Zero;
    }

    private void DriveMiddleAreaSizeChanged(object? sender, EventArgs args)
    {
        if (DriveMiddleArea.Height > 0)
        {
            DiagnosticsPanel.MaximumHeightRequest = DriveMiddleArea.Height;
        }
    }

    private void ApplyDriveMode(bool isDriving)
    {
        Shell.SetTabBarIsVisible(this, !isDriving);
        _displayMode.Apply(isDriving);
    }
}
