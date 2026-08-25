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
        base.OnCreate();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
