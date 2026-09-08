using DeveMobileLPR.App.Services;

namespace DeveMobileLPR.App.Behaviors;

/// <summary>
/// Declaratively animates a vertically expanding view. The behavior is attached
/// to the layout whose height should participate in the parent layout pass.
/// MAUI behaviors do not inherit a binding context, so bind <see cref="IsShown"/>
/// through an explicit source such as an <c>x:Reference</c> to the owning view.
/// </summary>
public sealed class AnimatedVisibilityBehavior : Behavior<VisualElement>
{
    private const string HeightAnimationName = "AnimatedVisibilityHeight";
    private VisualElement? _view;
    private int _transitionId;
    private bool _waitingForLayout;

    public static readonly BindableProperty IsShownProperty = BindableProperty.Create(
        nameof(IsShown),
        typeof(bool),
        typeof(AnimatedVisibilityBehavior),
        false,
        propertyChanged: OnIsShownChanged);

    public bool IsShown
    {
        get => (bool)GetValue(IsShownProperty);
        set => SetValue(IsShownProperty, value);
    }

    public uint EnterDuration { get; set; } = 220;

    public uint ExitDuration { get; set; } = 160;

    public Easing EnterEasing { get; set; } = Easing.CubicOut;

    public Easing ExitEasing { get; set; } = Easing.CubicIn;

    public double HiddenOpacity { get; set; } = 0;

    public double HiddenTranslationY { get; set; } = -12;

    public double HiddenScale { get; set; } = 0.98;

    public double VisibleOpacity { get; set; } = 1;

    public double VisibleTranslationY { get; set; } = 0;

    public double VisibleScale { get; set; } = 1;

    protected override void OnAttachedTo(VisualElement bindable)
    {
        base.OnAttachedTo(bindable);
        _view = bindable;
        bindable.SizeChanged += ViewSizeChanged;
        ApplyHiddenState(bindable);
        if (IsShown)
        {
            QueueTransition(true);
        }
    }

    protected override void OnDetachingFrom(VisualElement bindable)
    {
        bindable.SizeChanged -= ViewSizeChanged;
        bindable.AbortAnimation(HeightAnimationName);
        _transitionId++;
        _view = null;
        base.OnDetachingFrom(bindable);
    }

    private static void OnIsShownChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is AnimatedVisibilityBehavior behavior)
        {
            behavior.QueueTransition((bool)newValue);
        }
    }

    private void ViewSizeChanged(object? sender, EventArgs args)
    {
        // A page can receive its binding before its first layout pass. Retry the
        // initial expansion once a usable width is available so the measured
        // height is based on the actual device width.
        if (IsShown
            && _waitingForLayout
            && _view is { Width: > 0, IsVisible: true } view)
        {
            _waitingForLayout = false;
            QueueTransition(true);
        }
    }

    private void QueueTransition(bool show)
    {
        var view = _view;
        if (view is null)
        {
            return;
        }

        view.Dispatcher.Dispatch(() =>
            TransitionAsync(view, show, ++_transitionId).ObserveFailure("Animation"));
    }

    private async Task TransitionAsync(VisualElement view, bool show, int transitionId)
    {
        view.AbortAnimation(HeightAnimationName);
        view.CancelAnimations();

        if (show)
        {
            view.IsVisible = true;
            view.HeightRequest = 0;
            view.Opacity = HiddenOpacity;
            view.TranslationY = HiddenTranslationY;
            view.Scale = HiddenScale;

            var expandedHeight = MeasureExpandedHeight(view);
            if (expandedHeight <= 0)
            {
                _waitingForLayout = true;
                return;
            }
            _waitingForLayout = false;

            await Task.WhenAll(
                AnimateHeightAsync(view, 0, expandedHeight, EnterDuration, EnterEasing),
                view.FadeToAsync(VisibleOpacity, EnterDuration, EnterEasing),
                view.TranslateToAsync(0, VisibleTranslationY, EnterDuration, EnterEasing),
                view.ScaleToAsync(VisibleScale, EnterDuration, EnterEasing));

            if (transitionId == _transitionId && IsShown)
            {
                view.HeightRequest = -1;
            }

            return;
        }

        _waitingForLayout = false;
        var currentHeight = view.Height > 0
            ? view.Height
            : MeasureExpandedHeight(view);
        if (currentHeight <= 0)
        {
            ApplyHiddenState(view);
            return;
        }

        await Task.WhenAll(
            AnimateHeightAsync(view, currentHeight, 0, ExitDuration, ExitEasing),
            view.FadeToAsync(HiddenOpacity, ExitDuration, ExitEasing),
            view.TranslateToAsync(0, HiddenTranslationY, ExitDuration, ExitEasing),
            view.ScaleToAsync(HiddenScale, ExitDuration, ExitEasing));

        if (transitionId == _transitionId || !IsShown)
        {
            ApplyHiddenState(view);
        }
    }

    private double MeasureExpandedHeight(VisualElement view)
    {
        var width = view.Width > 0
            ? view.Width
            : view.Parent is VisualElement parent
                ? parent.Width
                : -1;
        if (width <= 0)
        {
            return 0;
        }

        // Measure the host itself after temporarily clearing its collapsed request. This
        // includes the child's padding/stroke and avoids a mismatch between the animated row
        // height and the natural height MAUI assigns once the animation finishes.
        var requestedHeight = view.HeightRequest;
        try
        {
            view.HeightRequest = -1;
            var expandedHeight = view.Measure(width, double.PositiveInfinity).Height;
            return double.IsFinite(expandedHeight) ? expandedHeight : 0;
        }
        finally
        {
            view.HeightRequest = requestedHeight;
        }
    }

    private void ApplyHiddenState(VisualElement view)
    {
        view.IsVisible = false;
        view.HeightRequest = 0;
        view.Opacity = HiddenOpacity;
        view.TranslationY = HiddenTranslationY;
        view.Scale = HiddenScale;
    }

    private static Task AnimateHeightAsync(
        VisualElement view,
        double start,
        double end,
        uint duration,
        Easing easing)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new Animation(value => view.HeightRequest = value, start, end);
        animation.Commit(
            view,
            HeightAnimationName,
            length: duration,
            easing: easing,
            finished: (_, _) => completion.TrySetResult(true));
        return completion.Task;
    }
}
