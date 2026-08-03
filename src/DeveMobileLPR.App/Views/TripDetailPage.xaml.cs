using DeveMobileLPR.App.ViewModels;
using DeveMobileLPR.Application;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.App.Views;

public partial class TripDetailPage : ContentPage
{
    private readonly TripDetailViewModel _viewModel;
    private readonly ISightingRepository _repository;
    private readonly IContextualSnapshotStore _snapshotStore;

    internal TripDetailPage(HistoryViewModel history, long tripId)
    {
        InitializeComponent();
        _repository = history.Coordinator.Repository;
        _snapshotStore = history.Coordinator.SnapshotStore;
        BindingContext = _viewModel = new TripDetailViewModel(_repository, tripId);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }

    private async void BackClicked(object? sender, EventArgs args) => await Navigation.PopAsync();

    private async void OpenRouteClicked(object? sender, EventArgs args)
    {
        if (_viewModel.RouteDestination is { } location) await OpenMapAsync(location);
    }

    private async void OpenLocationClicked(object? sender, EventArgs args)
    {
        if (sender is Button { CommandParameter: GeoPoint location }) await OpenMapAsync(location);
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
        await Navigation.PushAsync(new VehicleDetailPage(_repository, _snapshotStore, vehicle.NormalizedPlate));
    }

    private async Task OpenMapAsync(GeoPoint location)
    {
        try
        {
            await Microsoft.Maui.ApplicationModel.Map.Default.OpenAsync(
                location.Latitude,
                location.Longitude,
                new MapLaunchOptions { Name = "Vehicle sighting", NavigationMode = NavigationMode.None });
        }
        catch (Exception)
        {
            await DisplayAlertAsync("Map unavailable", "Install or enable a maps application to open this sighting.", "OK");
        }
    }
}
