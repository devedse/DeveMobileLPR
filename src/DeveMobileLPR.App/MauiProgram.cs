using DeveMobileLPR.App.Views;
using DeveMobileLPR.App.Controls;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Storage;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.App.ViewModels;

namespace DeveMobileLPR.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
    #if ANDROID
        builder.ConfigureMauiHandlers(handlers => handlers.AddHandler<CameraPreview, CameraPreviewHandler>());
    #elif WINDOWS
        builder.ConfigureMauiHandlers(handlers => handlers.AddHandler<CameraPreview, CameraPreviewHandler>());
    #endif
        builder.Services.AddSingleton<AppSettings>();
        builder.Services.AddSingleton<RecognitionTuningConfiguration>();
        builder.Services.AddSingleton<RdwDatabaseService>();
        builder.Services.AddSingleton(_ => new SqliteSightingRepository(Path.Combine(FileSystem.AppDataDirectory, "sightings.sqlite")));
        builder.Services.AddSingleton<DriveCoordinator>();
        builder.Services.AddSingleton<VideoAnalysisService>();
        builder.Services.AddSingleton(_ => new JsonVideoAnalysisRepository(Path.Combine(FileSystem.AppDataDirectory, "video-analyses")));
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
