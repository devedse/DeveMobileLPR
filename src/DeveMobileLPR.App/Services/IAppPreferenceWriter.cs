namespace DeveMobileLPR.App.Services;

internal interface IAppPreferenceWriter
{
    void Set(string key, float value);
    void Set(string key, string value);
}

internal sealed class MauiAppPreferenceWriter : IAppPreferenceWriter
{
    public void Set(string key, float value) => Preferences.Default.Set(key, value);
    public void Set(string key, string value) => Preferences.Default.Set(key, value);
}
