using DeveMobileLPR.App.Services;

namespace DeveMobileLPR.App.Platforms.Windows.Display;

internal sealed class WindowsDriveDisplayMode : IDriveDisplayMode
{
    public void Apply(bool isDriving)
    {
        // The Windows window remains user-resizable while driving.
    }
}
