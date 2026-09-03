using DeveMobileLPR.App.Services;
using DeveMobileLPR.App.ViewModels;
using DeveMobileLPR.Application;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.App.Views;

public partial class FullScreenMapPage : ContentPage
{
    private readonly ISightingRepository _repository;
    private readonly IVehicleImageStore _vehicleImageStore;

    internal FullScreenMapPage(
        ISightingRepository repository,
        IVehicleImageStore vehicleImageStore,
        HistoryMapViewModel map,
        string context,
        string title)
    {
        InitializeComponent();
        _repository = repository;
        _vehicleImageStore = vehicleImageStore;
        MapContext.Text = context;
        MapTitle.Text = title;
        HistoryMap.Map = map;
    }

    private async void BackClicked(object? sender, EventArgs args) =>
        await this.RunSafelyAsync("Could not close map", Navigation.PopAsync);

    private async void MapVehicleSelected(object? sender, string normalizedPlate) =>
        await this.RunSafelyAsync(
            "Could not open vehicle",
            () => Navigation.PushAsync(new VehicleDetailPage(_repository, _vehicleImageStore, normalizedPlate)));
}
