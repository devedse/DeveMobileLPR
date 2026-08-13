using Android.App;
using Android.Runtime;
using DeveMobileLPR.App.Services;

namespace DeveMobileLPR.App;

[Application]
public sealed class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    public override void OnCreate()
    {
        AppLogService.Initialize(FilesDir?.AbsolutePath ?? FileSystem.AppDataDirectory);
        AndroidEnvironment.UnhandledExceptionRaiser += (_, args) => AppLogService.RecordCrash(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                AppLogService.RecordCrash(exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) => AppLogService.RecordCrash(args.Exception);
        base.OnCreate();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
