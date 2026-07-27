using DeveMobileLPR.AndroidApp.ViewModels;

namespace DeveMobileLPR.AndroidApp.Views;

public partial class HistoryPage : ContentPage
{
    private readonly HistoryViewModel _viewModel;

    internal HistoryPage(HistoryViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }

    private async void TripSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (args.CurrentSelection.FirstOrDefault() is not TripCardViewModel trip) return;
        TripsList.SelectedItem = null;
        await Navigation.PushAsync(new TripDetailPage(_viewModel, trip.Id));
    }

    private async void VehicleSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (args.CurrentSelection.FirstOrDefault() is not VehicleCardViewModel vehicle) return;
        VehiclesList.SelectedItem = null;
        await Navigation.PushAsync(new VehicleDetailPage(_viewModel.Coordinator.Repository, vehicle.NormalizedPlate));
    }
}
