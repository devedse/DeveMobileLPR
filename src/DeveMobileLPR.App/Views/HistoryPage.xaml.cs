using DeveMobileLPR.App.ViewModels;

namespace DeveMobileLPR.App.Views;

public partial class HistoryPage : ContentPage
{
    private readonly HistoryViewModel _viewModel;
    private CancellationTokenSource? _tripLongPressCancellation;
    private long? _longPressedTripId;

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
        CancelTripLongPress();
        _viewModel.ClearTripSelection();
        base.OnDisappearing();
    }

    private async void TripTapped(object? sender, TappedEventArgs args)
    {
        if ((sender as TapGestureRecognizer)?.BindingContext is not TripCardViewModel trip) return;
        if (_longPressedTripId == trip.Id)
        {
            _longPressedTripId = null;
            return;
        }
        if (_viewModel.IsTripSelectionMode)
        {
            _viewModel.ToggleTripSelection(trip);
            return;
        }
        await Navigation.PushAsync(new TripDetailPage(_viewModel, trip.Id));
    }

    private void TripPointerPressed(object? sender, PointerEventArgs args)
    {
        CancelTripLongPress();
        if ((sender as PointerGestureRecognizer)?.BindingContext is not TripCardViewModel trip)
        {
            return;
        }
        _tripLongPressCancellation = new CancellationTokenSource();
        _ = BeginTripSelectionAfterDelayAsync(trip, _tripLongPressCancellation.Token);
    }

    private void TripPointerReleased(object? sender, PointerEventArgs args) => CancelTripLongPress();

    private async Task BeginTripSelectionAfterDelayAsync(TripCardViewModel trip, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(650), cancellationToken);
            _longPressedTripId = trip.Id;
            _viewModel.BeginTripSelection(trip);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelTripLongPress()
    {
        _tripLongPressCancellation?.Cancel();
        _tripLongPressCancellation?.Dispose();
        _tripLongPressCancellation = null;
    }

    private void CancelTripSelectionClicked(object? sender, EventArgs args) =>
        _viewModel.ClearTripSelection();

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
