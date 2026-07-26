using DeveMobileLPR.AndroidApp.ViewModels;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.AndroidApp.Views;

public partial class VehicleDetailPage : ContentPage
{
    private readonly VehicleDetailViewModel _viewModel;

    internal VehicleDetailPage(HistoryViewModel history, string normalizedPlate)
    {
        InitializeComponent();
        BindingContext = _viewModel = new VehicleDetailViewModel(history.Coordinator.Repository, normalizedPlate);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }

    private async void BackClicked(object? sender, EventArgs args) => await Navigation.PopAsync();

    private static async void OpenLocationClicked(object? sender, EventArgs args)
    {
        if (sender is Button { CommandParameter: GeoPoint location })
        {
            await Microsoft.Maui.ApplicationModel.Map.Default.OpenAsync(location.Latitude, location.Longitude, new MapLaunchOptions { Name = "Vehicle sighting", NavigationMode = NavigationMode.None });
        }
    }
}
