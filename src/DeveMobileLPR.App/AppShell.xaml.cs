using DeveMobileLPR.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DeveMobileLPR.App;

public partial class AppShell : Shell
{
    public AppShell(IServiceProvider services)
    {
        InitializeComponent();
        Items.Add(new TabBar
        {
            Items =
            {
                Tab("Drive", "drive.svg", () => services.GetRequiredService<DrivePage>()),
                Tab("Analyze", "analyze.svg", () => services.GetRequiredService<AnalyzePage>()),
                Tab("History", "history.svg", () => services.GetRequiredService<HistoryPage>()),
                Tab("Settings", "settings.svg", () => services.GetRequiredService<SettingsPage>())
            }
        });
    }

    private static ShellContent Tab(string title, string icon, Func<Page> factory) => new()
    {
        Title = title,
        Icon = icon,
        ContentTemplate = new DataTemplate(factory)
    };
}
