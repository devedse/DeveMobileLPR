using DeveMobileLPR.App.Services;
using DeveMobileLPR.App.ViewModels;
using DeveMobileLPR.Application;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.App.Views;

public partial class VehicleDetailPage : ContentPage
{
    private readonly VehicleDetailViewModel _viewModel;
    private readonly ISightingRepository _repository;
    private readonly IVehicleImageStore _vehicleImageStore;
    private bool _isOpeningMap;

    internal VehicleDetailPage(
        ISightingRepository repository,
        IVehicleImageStore vehicleImageStore,
        string normalizedPlate)
    {
        InitializeComponent();
        _repository = repository;
        _vehicleImageStore = vehicleImageStore;
        BindingContext = _viewModel = new VehicleDetailViewModel(repository, vehicleImageStore, normalizedPlate);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await this.RunSafelyAsync(
            "Could not load vehicle",
            _viewModel.LoadAsync);
    }

    private async void BackClicked(object? sender, EventArgs args) =>
        await this.RunSafelyAsync("Could not close vehicle", Navigation.PopAsync);

    private async void OpenLocationClicked(object? sender, EventArgs args)
    {
        if (sender is Button { CommandParameter: GeoPoint location })
        {
            await this.RunSafelyAsync(
                "Could not open location",
                () => this.OpenVehicleMapAsync(location));
        }
    }

    private async void MapRequested(object? sender, EventArgs args)
    {
        if (_isOpeningMap || _viewModel.Map is not { } map) return;
        await this.RunSafelyAsync(
            "Could not open vehicle map",
            async () =>
            {
                _isOpeningMap = true;
                try
                {
                    await Navigation.PushAsync(new FullScreenMapPage(
                        _repository,
                        _vehicleImageStore,
                        map,
                        "Vehicle sightings",
                        _viewModel.DisplayPlate));
                }
                finally
                {
                    _isOpeningMap = false;
                }
            });
    }
}
