using DeveMobileLPR.App.ViewModels;

namespace DeveMobileLPR.App.Views;

public partial class HistoryPage : ContentPage
{
    private readonly HistoryViewModel _viewModel;
    private long? _longPressedTripId;
#if ANDROID
    private readonly Dictionary<Android.Views.View, EventHandler<Android.Views.View.LongClickEventArgs>> _androidLongClickHandlers = [];
#endif

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
        if (args.Parameter is not TripCardViewModel trip) return;
        if (_longPressedTripId == trip.Id)
        {
            _longPressedTripId = null;
            return;
        }
        if (!OperatingSystem.IsWindows() && _viewModel.IsTripSelectionMode)
        {
            _viewModel.ToggleTripSelection(trip);
            return;
        }
        await Navigation.PushAsync(new TripDetailPage(_viewModel, trip.Id));
    }

    private void TripCardHandlerChanged(object? sender, EventArgs args)
    {
#if ANDROID
        if (sender is not Grid card || card.Handler?.PlatformView is not Android.Views.View platformView)
        {
            return;
        }
        if (_androidLongClickHandlers.TryGetValue(platformView, out var previousHandler))
        {
            platformView.LongClick -= previousHandler;
        }
        EventHandler<Android.Views.View.LongClickEventArgs> handler = (_, eventArgs) =>
        {
            if (card.BindingContext is not TripCardViewModel trip) return;
            _longPressedTripId = trip.Id;
            _viewModel.BeginTripSelection(trip);
            eventArgs.Handled = true;
        };
        _androidLongClickHandlers[platformView] = handler;
        platformView.LongClickable = true;
        platformView.LongClick += handler;
#endif
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
