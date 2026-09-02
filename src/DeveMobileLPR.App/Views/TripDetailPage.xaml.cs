using DeveMobileLPR.App.Services;
using DeveMobileLPR.App.ViewModels;
using DeveMobileLPR.Application;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.App.Views;

public partial class TripDetailPage : ContentPage
{
    private readonly TripDetailViewModel _viewModel;
    private readonly ISightingRepository _repository;
    private readonly IVehicleImageStore _vehicleImageStore;
    private bool _isOpeningMap;

    internal TripDetailPage(HistoryViewModel history, long tripId)
    {
        InitializeComponent();
        _repository = history.Coordinator.Repository;
        _vehicleImageStore = history.Coordinator.VehicleImageStore;
        BindingContext = _viewModel = new TripDetailViewModel(_repository, _vehicleImageStore, tripId);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await this.RunSafelyAsync(
            "Could not load trip",
            _viewModel.LoadAsync);
    }

    private async void BackClicked(object? sender, EventArgs args) =>
        await this.RunSafelyAsync("Could not close trip", Navigation.PopAsync);

    private async void OpenRouteClicked(object? sender, EventArgs args)
    {
        if (_viewModel.RouteDestination is { } location)
        {
            await this.RunSafelyAsync(
                "Could not open route",
                () => this.OpenVehicleMapAsync(location));
        }
    }

    private async void OpenLocationClicked(object? sender, EventArgs args)
    {
        if (sender is Button { CommandParameter: GeoPoint location })
        {
            await this.RunSafelyAsync(
                "Could not open location",
                () => this.OpenVehicleMapAsync(location));
        }
    }

    private async void VehicleSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (args.CurrentSelection.FirstOrDefault() is not TripVehicleCardViewModel vehicle) return;
        VehiclesList.SelectedItem = null;
        await this.RunSafelyAsync(
            "Could not open vehicle",
            () => Navigation.PushAsync(new VehicleDetailPage(_repository, _vehicleImageStore, vehicle.NormalizedPlate)));
    }

    private async void MapRequested(object? sender, EventArgs args)
    {
        if (_isOpeningMap || _viewModel.Map is not { } map) return;
        await this.RunSafelyAsync(
            "Could not open trip map",
            async () =>
            {
                _isOpeningMap = true;
                try
                {
                    await Navigation.PushAsync(new FullScreenMapPage(
                        _repository,
                        _vehicleImageStore,
                        map,
                        "Trip map",
                        _viewModel.Title));
                }
                finally
                {
                    _isOpeningMap = false;
                }
            });
    }

}
