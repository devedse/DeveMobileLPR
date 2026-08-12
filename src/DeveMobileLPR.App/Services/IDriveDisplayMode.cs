namespace DeveMobileLPR.App.Services;

/// <summary>Applies platform window/chrome behavior while a drive is active.</summary>
internal interface IDriveDisplayMode
{
    void Apply(bool isDriving);
}
