using AndroidX.Lifecycle;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

/// <summary>
/// Keeps CameraX active while either the app is visible or the opted-in foreground
/// service is running. CameraX can bind once and survive the activity being stopped.
/// </summary>
internal sealed class AndroidCameraLifecycleOwner : Java.Lang.Object, ILifecycleOwner
{
    private readonly LifecycleRegistry _registry;
    private bool _activityActive;
    private bool _serviceActive;
    private bool _resumed;

    public AndroidCameraLifecycleOwner()
    {
        _registry = new LifecycleRegistry(this);
        _registry.HandleLifecycleEvent(Lifecycle.Event.OnCreate!);
    }

    public Lifecycle Lifecycle => _registry;

    public void SetActivityActive(bool active) => SetState(activityActive: active, serviceActive: null);

    public void SetServiceActive(bool active) => SetState(activityActive: null, serviceActive: active);

    private void SetState(bool? activityActive, bool? serviceActive)
    {
        void Apply()
        {
            if (activityActive.HasValue)
            {
                _activityActive = activityActive.Value;
            }
            if (serviceActive.HasValue)
            {
                _serviceActive = serviceActive.Value;
            }

            var shouldResume = _activityActive || _serviceActive;
            if (shouldResume == _resumed)
            {
                return;
            }

            if (shouldResume)
            {
                _registry.HandleLifecycleEvent(Lifecycle.Event.OnStart!);
                _registry.HandleLifecycleEvent(Lifecycle.Event.OnResume!);
            }
            else
            {
                _registry.HandleLifecycleEvent(Lifecycle.Event.OnPause!);
                _registry.HandleLifecycleEvent(Lifecycle.Event.OnStop!);
            }
            _resumed = shouldResume;
        }

        if (MainThread.IsMainThread)
        {
            Apply();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(Apply);
        }
    }
}
