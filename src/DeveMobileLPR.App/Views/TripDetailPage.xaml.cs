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
        await _viewModel.LoadAsync();
    }

    private async void BackClicked(object? sender, EventArgs args) => await Navigation.PopAsync();

    private async void OpenRouteClicked(object? sender, EventArgs args)
    {
        if (_viewModel.RouteDestination is { } location) await this.OpenVehicleMapAsync(location);
    }

    private async void OpenLocationClicked(object? sender, EventArgs args)
    {
        if (sender is Button { CommandParameter: GeoPoint location }) await this.OpenVehicleMapAsync(location);
    }

    private async void SortClicked(object? sender, EventArgs args)
    {
        var selected = await DisplayActionSheetAsync(
            "Sort vehicles",
            "Cancel",
            null,
            _viewModel.SortOptions.ToArray());
        if (selected is not null && selected != "Cancel")
        {
            _viewModel.SelectedSort = selected;
        }
    }

    private async void VehicleSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (args.CurrentSelection.FirstOrDefault() is not TripVehicleCardViewModel vehicle) return;
        VehiclesList.SelectedItem = null;
        await Navigation.PushAsync(new VehicleDetailPage(_repository, _vehicleImageStore, vehicle.NormalizedPlate));
    }

    private async void MapRequested(object? sender, EventArgs args)
    {
        if (_viewModel.Map is not { } map) return;
        await Navigation.PushAsync(new FullScreenTripMapPage(
            _repository,
            _vehicleImageStore,
            map,
            _viewModel.Title));
    }

}
