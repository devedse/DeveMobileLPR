using Android.Views;
using DeveMobileLPR.App.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace DeveMobileLPR.App.Platforms.Android.History;

internal sealed class AndroidTripCardViewHandler : BorderHandler
{
    private GestureDetector? _detector;
    private EventHandler<global::Android.Views.View.TouchEventArgs>? _touchHandler;

    protected override void ConnectHandler(ContentViewGroup platformView)
    {
        base.ConnectHandler(platformView);
        var card = (TripCardView)VirtualView;
        card.SetUsesNativeGestures(true);
        var detector = new GestureDetector(
            platformView.Context,
            new TripGestureListener(card.SendNativeLongPressed, card.SendNativeTapped));
        _detector = detector;
        ApplyTouchFeedback(platformView);
        _touchHandler = (_, eventArgs) =>
        {
            if (eventArgs.Event is not { } motionEvent)
            {
                return;
            }

            if (motionEvent.ActionMasked == MotionEventActions.Down)
            {
                platformView.Pressed = true;
            }
            else if (motionEvent.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
            {
                platformView.PostDelayed(() => platformView.Pressed = false, 80);
            }
            eventArgs.Handled = detector.OnTouchEvent(motionEvent);
        };
        platformView.Touch += _touchHandler;
    }

    protected override void DisconnectHandler(ContentViewGroup platformView)
    {
        if (_touchHandler is not null)
        {
            platformView.Touch -= _touchHandler;
            _touchHandler = null;
        }
        _detector?.Dispose();
        _detector = null;
        ((TripCardView)VirtualView).SetUsesNativeGestures(false);
        base.DisconnectHandler(platformView);
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
        : GestureDetector.SimpleOnGestureListener
    {
        public override bool OnDown(MotionEvent? e) => true;
        public override void OnLongPress(MotionEvent? e) => longPressed();

        public override bool OnSingleTapUp(MotionEvent? e)
        {
            tapped();
            return true;
        }
    }
}
