using DeveMobileLPR.App.Services;
using DeveMobileLPR.App.ViewModels;

namespace DeveMobileLPR.App.Views;

public partial class DrivePage : ContentPage
{
    private readonly DriveViewModel _viewModel;
    private readonly IDriveDisplayMode _displayMode;
    private bool _closing;

    internal DrivePage(DriveViewModel viewModel, IDriveDisplayMode displayMode)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _displayMode = displayMode;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.DriveModeChanged += DriveModeChanged;
        ApplyDriveMode(true);
        await _viewModel.InitializeAsync();
        await _viewModel.StartDriveAsync();
        if (!_viewModel.IsDriving)
        {
            await CloseOnceAsync();
        }
    }

    protected override void OnDisappearing()
    {
        _viewModel.DriveModeChanged -= DriveModeChanged;
        base.OnDisappearing();
    }

    private async void DriveModeChanged(object? sender, bool isDriving)
    {
        ApplyDriveMode(isDriving);
        if (!isDriving)
        {
            await CloseOnceAsync();
        }
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

    protected override bool OnBackButtonPressed()
    {
        _ = StopAndCloseAsync();
        return true;
    }

    private async Task StopAndCloseAsync()
    {
        if (_viewModel.IsDriving)
        {
            _viewModel.ToggleDriveCommand.Execute(null);
            return;
        }

        await CloseOnceAsync();
    }

    private async Task CloseOnceAsync()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            if (Navigation.ModalStack.Contains(Parent))
            {
                await Navigation.PopModalAsync();
            }
        }
        finally
        {
            _closing = false;
        }
    }
}
