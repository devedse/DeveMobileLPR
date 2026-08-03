using DeveMobileLPR.App.ViewModels;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.App.Views;

public partial class VehicleDetailPage : ContentPage
{
    private readonly VehicleDetailViewModel _viewModel;

    internal VehicleDetailPage(ISightingRepository repository, string normalizedPlate)
    {
        InitializeComponent();
        BindingContext = _viewModel = new VehicleDetailViewModel(repository, normalizedPlate);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }

    private async void BackClicked(object? sender, EventArgs args) => await Navigation.PopAsync();

    private async void OpenLocationClicked(object? sender, EventArgs args)
    {
        if (sender is Button { CommandParameter: GeoPoint location })
        {
            await this.OpenVehicleMapAsync(location);
        }
    }
}
