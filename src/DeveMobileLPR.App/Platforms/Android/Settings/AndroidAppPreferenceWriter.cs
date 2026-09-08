using DeveMobileLPR.App.Services;

namespace DeveMobileLPR.App.Platforms.Android.Settings;

internal sealed class AndroidAppPreferenceWriter : IAppPreferenceWriter
{
    public void Set(string key, float value) =>
        GetPreferences()?.Edit()?.PutFloat(key, value)?.Commit();

    public void Set(string key, string value) =>
        GetPreferences()?.Edit()?.PutString(key, value)?.Commit();

    private static global::Android.Content.ISharedPreferences? GetPreferences()
    {
        var context = global::Android.App.Application.Context;
        return context.GetSharedPreferences(
            $"{context.PackageName}_preferences",
            global::Android.Content.FileCreationMode.Private);
    }
}
