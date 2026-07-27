using DeveMobileLPR.AndroidApp.ViewModels;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.AndroidApp.Views;

public partial class VehicleDetailPage : ContentPage
{
    private readonly VehicleDetailViewModel _viewModel;

    internal VehicleDetailPage(SqliteSightingRepository repository, string normalizedPlate)
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
            await OpenMapAsync(location);
        }
    }

    private async Task OpenMapAsync(GeoPoint location)
    {
        try
        {
            await Microsoft.Maui.ApplicationModel.Map.Default.OpenAsync(location.Latitude, location.Longitude, new MapLaunchOptions { Name = "Vehicle sighting", NavigationMode = NavigationMode.None });
        }
        catch (Exception)
        {
            await DisplayAlertAsync("Map unavailable", "Install or enable a maps application to open this sighting.", "OK");
        }
    }
}
