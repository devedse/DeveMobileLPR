using DeveMobileLPR.App.Services;
using DeveMobileLPR.App.ViewModels;

namespace DeveMobileLPR.App.Views;

public partial class HistoryPage : ContentPage
{
    private readonly HistoryViewModel _viewModel;
    private readonly ITripCardGestureAdapter _tripCardGestures;

    internal HistoryPage(HistoryViewModel viewModel, ITripCardGestureAdapter tripCardGestures)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _tripCardGestures = tripCardGestures;
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
        // The native adapter owns Android's full touch stream to distinguish taps from long presses.
        if (_tripCardGestures.HandlesTap) return;
        if (args.Parameter is not TripCardViewModel trip) return;
        await OpenOrSelectTripAsync(trip);
    }

    private void TripCardHandlerChanged(object? sender, EventArgs args)
    {
        if (sender is not Border card)
        {
            return;
        }

        _tripCardGestures.Attach(
            card,
            () => MainThread.BeginInvokeOnMainThread(() =>
            {
                if (card.BindingContext is TripCardViewModel trip)
                {
                    _viewModel.BeginTripSelection(trip);
                }
            }),
            () => MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (card.BindingContext is TripCardViewModel trip)
                {
                    await OpenOrSelectTripAsync(trip);
                }
            }));
    }

    private async Task OpenOrSelectTripAsync(TripCardViewModel trip)
    {
        if (_tripCardGestures.HandlesTap && _viewModel.IsTripSelectionMode)
        {
            _viewModel.ToggleTripSelection(trip);
            return;
        }
        await Navigation.PushAsync(new TripDetailPage(_viewModel, trip.Id));
    }


    private async void RemoveTripsClicked(object? sender, EventArgs args)
    {
        var count = _viewModel.SelectedTripCount;
        if (count == 0) return;
        var confirmed = await DisplayAlertAsync(
            "Remove trips?",
            $"Are you sure you want to remove {count} trips?\n\nAll cars + images scanned during these trips will be removed too.",
            "Remove",
            "Cancel");
        if (!confirmed) return;
        await _viewModel.DeleteSelectedTripsAsync();
    }

    private async void VehicleSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (args.CurrentSelection.FirstOrDefault() is not VehicleCardViewModel vehicle) return;
        VehiclesList.SelectedItem = null;
        await Navigation.PushAsync(new VehicleDetailPage(
            _viewModel.Coordinator.Repository,
            _viewModel.Coordinator.VehicleImageStore,
            vehicle.NormalizedPlate));
    }
}
