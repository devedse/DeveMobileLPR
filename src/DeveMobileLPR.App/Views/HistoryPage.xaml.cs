using DeveMobileLPR.App.Controls;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.App.ViewModels;

namespace DeveMobileLPR.App.Views;

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

    protected override void OnDisappearing()
    {
        _viewModel.ClearTripSelection();
        base.OnDisappearing();
    }

    private async void TripTapped(object? sender, TappedEventArgs args)
    {
        if (args.Parameter is TripCardViewModel trip)
        {
            await this.RunSafelyAsync(
                "Could not open trip",
                () => OpenOrSelectTripAsync(trip, nativeGesture: false));
        }
    }

    private void TripLongPressed(object? sender, EventArgs args)
    {
        if (sender is TripCardView { BindingContext: TripCardViewModel trip })
        {
            _viewModel.BeginTripSelection(trip);
        }
    }

    private async void NativeTripTapped(object? sender, EventArgs args)
    {
        if (sender is TripCardView { BindingContext: TripCardViewModel trip })
        {
            await this.RunSafelyAsync(
                "Could not open trip",
                () => OpenOrSelectTripAsync(trip, nativeGesture: true));
        }
    }

    private async Task OpenOrSelectTripAsync(TripCardViewModel trip, bool nativeGesture)
    {
        if (nativeGesture && _viewModel.IsTripSelectionMode)
        {
            _viewModel.ToggleTripSelection(trip);
            return;
        }
        await Navigation.PushAsync(new TripDetailPage(_viewModel, trip.Id));
    }


    private async void RemoveTripsClicked(object? sender, EventArgs args) =>
        await this.RunSafelyAsync(
            "Could not remove trips",
            async () =>
            {
                var count = _viewModel.SelectedTripCount;
                if (count == 0)
                {
                    return;
                }
                var confirmed = await DisplayAlertAsync(
                    "Remove trips?",
                    $"Are you sure you want to remove {count} trips?\n\nAll cars + images scanned during these trips will be removed too.",
                    "Remove",
                    "Cancel");
                if (confirmed)
                {
                    await _viewModel.DeleteSelectedTripsAsync();
                }
            });

    private async void VehicleSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (args.CurrentSelection.FirstOrDefault() is not VehicleCardViewModel vehicle) return;
        VehiclesList.SelectedItem = null;
        await this.RunSafelyAsync(
            "Could not open vehicle",
            () => Navigation.PushAsync(new VehicleDetailPage(
                _viewModel.Coordinator.Repository,
                _viewModel.Coordinator.VehicleImageStore,
                vehicle.NormalizedPlate)));
    }
}
