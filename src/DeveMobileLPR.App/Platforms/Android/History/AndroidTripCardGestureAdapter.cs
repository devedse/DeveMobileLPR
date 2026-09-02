using DeveMobileLPR.App.Services;

namespace DeveMobileLPR.App.Platforms.Android.History;

internal sealed class AndroidTripCardGestureAdapter : ITripCardGestureAdapter
{
    private readonly Dictionary<global::Android.Views.View, EventHandler<global::Android.Views.View.TouchEventArgs>> _handlers = [];

    public bool HandlesTap => true;

    public void Attach(Border card, Action longPressed, Action tapped)
    {
        if (card.Handler?.PlatformView is not global::Android.Views.View platformView)
        {
            return;
        }
        if (_handlers.TryGetValue(platformView, out var previousHandler))
        {
            platformView.Touch -= previousHandler;
        }

        var detector = new global::Android.Views.GestureDetector(
            platformView.Context,
            new TripGestureListener(longPressed, tapped));
        ApplyTouchFeedback(platformView);
        EventHandler<global::Android.Views.View.TouchEventArgs> handler = (_, eventArgs) =>
        {
            if (eventArgs.Event is not { } motionEvent)
            {
                return;
            }

            if (motionEvent.ActionMasked == global::Android.Views.MotionEventActions.Down)
            {
                platformView.Pressed = true;
            }
            else if (motionEvent.ActionMasked is global::Android.Views.MotionEventActions.Up
                     or global::Android.Views.MotionEventActions.Cancel)
            {
                platformView.PostDelayed(() => platformView.Pressed = false, 80);
            }
            eventArgs.Handled = detector.OnTouchEvent(motionEvent);
        };
        _handlers[platformView] = handler;
        platformView.Touch += handler;
    }

    private static void ApplyTouchFeedback(global::Android.Views.View view)
    {
        view.Clickable = true;
        view.LongClickable = true;
        using var attribute = new global::Android.Util.TypedValue();
        if (view.Context?.Theme?.ResolveAttribute(
                global::Android.Resource.Attribute.SelectableItemBackground,
                attribute,
                true) == true
            && attribute.ResourceId != 0)
        {
            view.Foreground = view.Context.GetDrawable(attribute.ResourceId);
        }
    }

    private sealed class TripGestureListener(Action longPressed, Action tapped)
        : global::Android.Views.GestureDetector.SimpleOnGestureListener
    {
        public override bool OnDown(global::Android.Views.MotionEvent? e) => true;
        public override void OnLongPress(global::Android.Views.MotionEvent? e) => longPressed();

        public override bool OnSingleTapUp(global::Android.Views.MotionEvent? e)
        {
            tapped();
            return true;
        }
    }
}
