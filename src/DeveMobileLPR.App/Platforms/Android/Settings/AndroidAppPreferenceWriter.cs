using DeveMobileLPR.App.Services;

namespace DeveMobileLPR.App.Platforms.Android.Settings;

internal sealed class AndroidAppPreferenceWriter : IAppPreferenceWriter
{
    private readonly global::Android.Content.ISharedPreferences? _preferences;

    public AndroidAppPreferenceWriter()
    {
        var context = global::Android.App.Application.Context;
        _preferences = context.GetSharedPreferences(
            $"{context.PackageName}_preferences",
            global::Android.Content.FileCreationMode.Private);
    }

    public void Set(string key, float value) =>
        _preferences?.Edit()?.PutFloat(key, value)?.Commit();

    public void Set(string key, string value) =>
        _preferences?.Edit()?.PutString(key, value)?.Commit();
}
