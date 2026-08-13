using DeveMobileLPR.App.Views;
using DeveMobileLPR.App.Controls;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;
using DeveMobileLPR.Storage;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.App.ViewModels;
using DeveMobileLPR.App.Infrastructure;
using Plugin.Maui.Audio;
#if ANDROID
using DeveMobileLPR.App.Platforms.Android.Background;
using DeveMobileLPR.App.Platforms.Android.Camera;
using DeveMobileLPR.App.Platforms.Android.Display;
using DeveMobileLPR.App.Platforms.Android.Inference;
using DeveMobileLPR.App.Platforms.Android.Location;
using DeveMobileLPR.App.Platforms.Android.Settings;
using DeveMobileLPR.App.Platforms.Android.Video;
#elif WINDOWS
using DeveMobileLPR.App.Platforms.Windows.Camera;
using DeveMobileLPR.App.Platforms.Windows.Inference;
using DeveMobileLPR.App.Platforms.Windows.Settings;
using DeveMobileLPR.App.Platforms.Windows.Video;
#endif

namespace DeveMobileLPR.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        AppLogService.Initialize(FileSystem.AppDataDirectory);
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.AddAudio();
#if ANDROID
        builder.ConfigureMauiHandlers(handlers =>
        {
            handlers.AddHandler<CameraPreview, CameraPreviewHandler>();
            handlers.AddHandler<DualCameraPreview, DualCameraPreviewHandler>();
        });
#elif WINDOWS
        builder.ConfigureMauiHandlers(handlers => handlers.AddHandler<CameraPreview, CameraPreviewHandler>());
#endif
        builder.Services.AddSingleton<AppSettings>();
        builder.Services.AddSingleton<AppLogService>();
        builder.Services.AddSingleton<IApplicationLog>(services => services.GetRequiredService<AppLogService>());
        builder.Services.AddSingleton<IDriveSettings>(services => services.GetRequiredService<AppSettings>());
        builder.Services.AddSingleton<RecognitionTuningConfiguration>();
        builder.Services.AddSingleton<RdwDatabaseService>();
        builder.Services.AddSingleton<IVehicleDataStatus>(services => services.GetRequiredService<RdwDatabaseService>());
        builder.Services.AddSingleton(_ => new SightingRepository(Path.Combine(FileSystem.AppDataDirectory, "sightings.sqlite")));
        builder.Services.AddSingleton<ISightingRepository>(services => services.GetRequiredService<SightingRepository>());
        builder.Services.AddSingleton<IVehicleLookup>(services =>
            new AppVehicleLookup(services.GetRequiredService<RdwDatabaseService>().DatabasePath));
        builder.Services.AddSingleton<IApplicationDispatcher, MauiApplicationDispatcher>();
        builder.Services.AddSingleton<IDeviceExperience, MauiDeviceExperience>();
        builder.Services.AddSingleton<IVehicleImageEncoder, ImageSharpVehicleImageEncoder>();
#if ANDROID
        builder.Services.AddSingleton<AndroidCameraLifecycleOwner>();
        builder.Services.AddSingleton<IDriveSourceCatalog>(_ => new AndroidDriveSourceCatalog(global::Android.App.Application.Context));
        builder.Services.AddSingleton<IBackgroundScanningManager, AndroidBackgroundScanningManager>();
        builder.Services.AddSingleton<IDriveLocationTrackerFactory>(_ =>
            new AndroidLocationTrackerFactory(global::Android.App.Application.Context));
        builder.Services.AddSingleton<IRecognitionPipelineProvider, AndroidRecognitionPipelineProvider>();
        builder.Services.AddSingleton<IVideoFileBackend, AndroidVideoFileBackend>();
        builder.Services.AddSingleton<IPlatformSettingsInfo, AndroidPlatformSettingsInfo>();
        builder.Services.AddSingleton<IDriveDisplayMode, AndroidDriveDisplayMode>();
#elif WINDOWS
        builder.Services.AddSingleton<IBackgroundScanningManager, UnsupportedBackgroundScanningManager>();
        builder.Services.AddSingleton<IDriveLocationTrackerFactory, UnsupportedDriveLocationTrackerFactory>();
        builder.Services.AddSingleton<IRecognitionPipelineProvider, WindowsRecognitionPipelineProvider>();
        builder.Services.AddSingleton<IVideoFileBackend, WindowsVideoFileBackend>();
        builder.Services.AddSingleton<IPlatformSettingsInfo, WindowsPlatformSettingsInfo>();
        builder.Services.AddSingleton<IDriveDisplayMode, PassiveDriveDisplayMode>();
#endif
        builder.Services.AddSingleton<IVehicleImageStore>(services => new VehicleImageStore(
            FileSystem.AppDataDirectory,
            services.GetRequiredService<IVehicleImageEncoder>()));
        builder.Services.AddSingleton<DriveCoordinator>();
        builder.Services.AddSingleton<VideoAnalysisService>();
        builder.Services.AddSingleton(_ => new JsonVideoAnalysisRepository(Path.Combine(FileSystem.AppDataDirectory, "video-analyses")));
        builder.Services.AddSingleton<DriveViewModel>();
        builder.Services.AddSingleton<AnalyzeViewModel>();
        builder.Services.AddSingleton<HistoryViewModel>();
        builder.Services.AddSingleton<HistoryExportService>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<AppShell>();
        builder.Services.AddSingleton(services => new DrivePage(
            services.GetRequiredService<DriveViewModel>(),
            services.GetRequiredService<IDriveDisplayMode>()));
        builder.Services.AddSingleton(services => new AnalyzePage(services.GetRequiredService<AnalyzeViewModel>()));
        builder.Services.AddSingleton(services => new HistoryPage(services.GetRequiredService<HistoryViewModel>()));
        builder.Services.AddSingleton(services => new SettingsPage(
            services.GetRequiredService<SettingsViewModel>(),
            services.GetRequiredService<AppLogService>()));
        return builder.Build();
    }

}
