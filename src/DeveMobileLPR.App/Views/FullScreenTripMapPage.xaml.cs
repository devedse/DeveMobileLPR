using DeveMobileLPR.App.ViewModels;
using DeveMobileLPR.Application;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.App.Views;

public partial class FullScreenTripMapPage : ContentPage
{
    private readonly ISightingRepository _repository;
    private readonly IVehicleImageStore _vehicleImageStore;

    internal FullScreenTripMapPage(
        ISightingRepository repository,
        IVehicleImageStore vehicleImageStore,
        TripMapViewModel map,
        string tripTitle)
    {
        InitializeComponent();
        _repository = repository;
        _vehicleImageStore = vehicleImageStore;
        TripTitle.Text = tripTitle;
        HistoryMap.Map = map;
    }

    private async void BackClicked(object? sender, EventArgs args) => await Navigation.PopAsync();

    private async void MapVehicleSelected(object? sender, string normalizedPlate) =>
        await Navigation.PushAsync(new VehicleDetailPage(_repository, _vehicleImageStore, normalizedPlate));
}
