using DeveMobileLPR.App.ViewModels;

namespace DeveMobileLPR.App.Views;

public partial class HistoryPage : ContentPage
{
    private readonly HistoryViewModel _viewModel;
#if ANDROID
    private readonly Dictionary<Android.Views.View, EventHandler<Android.Views.View.TouchEventArgs>> _androidTouchHandlers = [];
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
        // Android taps are emitted by TripGestureListener, which must own the full touch stream
        // in order to distinguish a tap from a long press. The MAUI recognizer remains for Windows.
        if (OperatingSystem.IsAndroid()) return;
        if (args.Parameter is not TripCardViewModel trip) return;
        await OpenOrSelectTripAsync(trip);
    }

    private void TripCardHandlerChanged(object? sender, EventArgs args)
    {
#if ANDROID
        if (sender is not Border card || card.Handler?.PlatformView is not Android.Views.View platformView)
        {
            return;
        }
        if (_androidTouchHandlers.TryGetValue(platformView, out var previousHandler))
        {
            platformView.Touch -= previousHandler;
        }
        var detector = new Android.Views.GestureDetector(
            platformView.Context,
            new TripGestureListener(
                () => MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (card.BindingContext is not TripCardViewModel trip) return;
                    _viewModel.BeginTripSelection(trip);
                }),
                () => MainThread.BeginInvokeOnMainThread(async () =>
                {
                    if (card.BindingContext is TripCardViewModel trip)
                    {
                        await OpenOrSelectTripAsync(trip);
                    }
                })));
        ApplyAndroidTouchFeedback(platformView);
        EventHandler<Android.Views.View.TouchEventArgs> handler = (_, eventArgs) =>
        {
            if (eventArgs.Event is not { } motionEvent)
            {
                return;
            }

            if (motionEvent.ActionMasked == Android.Views.MotionEventActions.Down)
            {
                platformView.Pressed = true;
            }
            else if (motionEvent.ActionMasked is Android.Views.MotionEventActions.Up
                     or Android.Views.MotionEventActions.Cancel)
            {
                platformView.PostDelayed(() => platformView.Pressed = false, 80);
            }
            eventArgs.Handled = detector.OnTouchEvent(motionEvent);
        };
        _androidTouchHandlers[platformView] = handler;
        platformView.Touch += handler;
#endif
    }

#if ANDROID
    private static void ApplyAndroidTouchFeedback(Android.Views.View view)
    {
        view.Clickable = true;
        view.LongClickable = true;
        using var attribute = new Android.Util.TypedValue();
        if (view.Context?.Theme?.ResolveAttribute(
                Android.Resource.Attribute.SelectableItemBackground,
                attribute,
                true) == true
            && attribute.ResourceId != 0)
        {
            view.Foreground = view.Context.GetDrawable(attribute.ResourceId);
        }
    }
#endif

    private async Task OpenOrSelectTripAsync(TripCardViewModel trip)
    {
        if (!OperatingSystem.IsWindows() && _viewModel.IsTripSelectionMode)
        {
            _viewModel.ToggleTripSelection(trip);
            return;
        }
        await Navigation.PushAsync(new TripDetailPage(_viewModel, trip.Id));
    }

#if ANDROID
    private sealed class TripGestureListener(Action longPressed, Action tapped)
        : Android.Views.GestureDetector.SimpleOnGestureListener
    {
        public override bool OnDown(Android.Views.MotionEvent? e) => true;

        public override void OnLongPress(Android.Views.MotionEvent? e) => longPressed();

        public override bool OnSingleTapUp(Android.Views.MotionEvent? e)
        {
            tapped();
            return true;
        }
    }
#endif

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
