namespace DeveMobileLPR.App.Services;

/// <summary>
/// Shared display policy for targets that require no window changes while driving.
/// </summary>
internal sealed class PassiveDriveDisplayMode : IDriveDisplayMode
{
    public void Apply(bool isDriving)
    {
    }
}
