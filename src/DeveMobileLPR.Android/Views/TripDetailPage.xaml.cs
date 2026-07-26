using DeveMobileLPR.AndroidApp.ViewModels;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.AndroidApp.Views;

public partial class TripDetailPage : ContentPage
{
    private readonly TripDetailViewModel _viewModel;

    internal TripDetailPage(HistoryViewModel history, long tripId)
    {
        InitializeComponent();
        BindingContext = _viewModel = new TripDetailViewModel(history.Coordinator.Repository, tripId);
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

    private static Task OpenMapAsync(GeoPoint location) => Microsoft.Maui.ApplicationModel.Map.Default.OpenAsync(
        location.Latitude,
        location.Longitude,
        new MapLaunchOptions { Name = "Vehicle sighting", NavigationMode = NavigationMode.None });
}
