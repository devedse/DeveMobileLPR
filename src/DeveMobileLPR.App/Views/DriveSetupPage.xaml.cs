using DeveMobileLPR.App.ViewModels;

namespace DeveMobileLPR.App.Views;

public partial class DriveSetupPage : ContentPage
{
    private readonly DriveViewModel _viewModel;
    private readonly Func<DrivePage> _drivePageFactory;
    private bool _opening;

    internal DriveSetupPage(DriveViewModel viewModel, Func<DrivePage> drivePageFactory)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _drivePageFactory = drivePageFactory;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }

    private async void StartDriveClicked(object? sender, EventArgs args)
    {
        if (_opening || !_viewModel.CanStart)
        {
            return;
        }

        _opening = true;
        try
        {
            await Navigation.PushModalAsync(_drivePageFactory());
        }
        finally
        {
            _opening = false;
        }
    }
}
