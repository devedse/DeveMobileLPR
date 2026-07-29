namespace DeveMobileLPR.App;

using DeveMobileLPR.App.Resources.Styles;
using Microsoft.Extensions.DependencyInjection;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        ApplyTheme(RequestedTheme);
        RequestedThemeChanged += ThemeChanged;
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(_services.GetRequiredService<AppShell>());

    private void ThemeChanged(object? sender, AppThemeChangedEventArgs args) =>
        MainThread.BeginInvokeOnMainThread(() => ApplyTheme(args.RequestedTheme));

    private void ApplyTheme(AppTheme theme)
    {
        var useDark = theme == AppTheme.Dark;
        var current = Resources.MergedDictionaries.FirstOrDefault(dictionary => dictionary is DarkColors or LightColors);
        if (current is DarkColors == useDark && current is not null) return;
        var replacement = useDark ? new DarkColors() as ResourceDictionary : new LightColors();
        if (current is not null) Resources.MergedDictionaries.Remove(current);
        Resources.MergedDictionaries.Add(replacement);
    }
}
