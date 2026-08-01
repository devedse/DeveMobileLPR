using Android.Hardware.Display;
using AndroidX.Camera.View;

namespace DeveMobileLPR.App.Camera;

internal sealed class CameraXDisplayRotationListener(
    PreviewView previewView,
    Action changed) : Java.Lang.Object, DisplayManager.IDisplayListener
{
    public void OnDisplayAdded(int displayId)
    {
    }

    public void OnDisplayChanged(int displayId)
    {
        if (previewView.Display?.DisplayId == displayId)
        {
            previewView.Post(new Java.Lang.Runnable(changed));
        }
    }

    public void OnDisplayRemoved(int displayId)
    {
    }
}
