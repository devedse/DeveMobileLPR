using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.Views;

internal static class MapLaunchExtensions
{
    public static async Task OpenVehicleMapAsync(this ContentPage page, GeoPoint location)
    {
        try
        {
            await Microsoft.Maui.ApplicationModel.Map.Default.OpenAsync(
                location.Latitude,
                location.Longitude,
                new MapLaunchOptions { Name = "Vehicle sighting", NavigationMode = NavigationMode.None });
        }
        catch (Exception)
        {
            await page.DisplayAlertAsync(
                "Map unavailable",
                "Install or enable a maps application to open this sighting.",
                "OK");
        }
    }
}