using DeveMobileLPR.AndroidApp.Views;
using DeveMobileLPR.AndroidApp.Controls;
using DeveMobileLPR.AndroidApp.Services;
using DeveMobileLPR.Storage;
using DeveMobileLPR.AndroidApp.ViewModels;

namespace DeveMobileLPR.AndroidApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureMauiHandlers(handlers => handlers.AddHandler<CameraPreview, CameraPreviewHandler>());
        builder.Services.AddSingleton<AppSettings>();
        builder.Services.AddSingleton<RdwDatabaseService>();
        builder.Services.AddSingleton(_ => new SqliteSightingRepository(Path.Combine(FileSystem.AppDataDirectory, "sightings.sqlite")));
        builder.Services.AddSingleton<DriveCoordinator>();
        builder.Services.AddSingleton<VideoAnalysisService>();
        builder.Services.AddSingleton<DriveViewModel>();
        builder.Services.AddSingleton<AnalyzeViewModel>();
        builder.Services.AddSingleton<HistoryViewModel>();
        builder.Services.AddSingleton<HistoryExportService>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<AppShell>();
        builder.Services.AddSingleton(services => new DrivePage(services.GetRequiredService<DriveViewModel>()));
        builder.Services.AddSingleton(services => new AnalyzePage(services.GetRequiredService<AnalyzeViewModel>()));
        builder.Services.AddSingleton(services => new HistoryPage(services.GetRequiredService<HistoryViewModel>()));
        builder.Services.AddSingleton(services => new SettingsPage(services.GetRequiredService<SettingsViewModel>()));
        return builder.Build();
    }
}
