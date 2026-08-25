using DeveMobileLPR.App.Views;
using DeveMobileLPR.App.Controls;
using DeveMobileLPR.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DeveMobileLPR.App;

public partial class AppShell : Shell
{
    public AppShell(IServiceProvider services, InferenceBackendStatus backendStatus)
    {
        InitializeComponent();
        Items.Add(new TabBar
        {
            Items =
            {
                Tab("Drive", "drive.svg", () => services.GetRequiredService<DrivePage>(), backendStatus),
                Tab("Analyze", "analyze.svg", () => services.GetRequiredService<AnalyzePage>(), backendStatus),
                Tab("History", "history.svg", () => services.GetRequiredService<HistoryPage>(), backendStatus),
                Tab("Settings", "settings.svg", () => services.GetRequiredService<SettingsPage>(), backendStatus)
            }
        });
    }

    private static ShellContent Tab(
        string title,
        string icon,
        Func<Page> factory,
        InferenceBackendStatus backendStatus) => new()
    {
        Title = title,
        Icon = icon,
        ContentTemplate = new DataTemplate(() =>
        {
            var page = factory();
            Shell.SetNavBarIsVisible(page, true);
            Shell.SetTitleView(page, new InferenceBackendBanner
            {
                BindingContext = backendStatus
            });
            return page;
        })
    };
}
