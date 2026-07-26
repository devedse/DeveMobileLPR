namespace DeveMobileLPR.AndroidApp.Controls;

internal sealed class CameraPreview : View
{
    public CameraPreview()
    {
        AutomationId = "drive_camera_preview";
        SemanticProperties.SetDescription(this, "Live camera preview with on-device license plate detections");
    }
}
