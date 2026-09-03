using System.Text;
using DeveMobileLPR.Application;

namespace DeveMobileLPR.App.Services;

internal sealed class AppLogService : IApplicationLog
{
    private const int MaximumRecentEntries = 250;
    private const long MaximumFileBytes = 1_000_000;
    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();
    private static string? _path;
    private static bool _globalExceptionHandlersRegistered;

    public static event EventHandler? Changed;

    public static void Initialize(string directory)
    {
        lock (Gate)
        {
            if (!_globalExceptionHandlersRegistered)
            {
                AppDomain.CurrentDomain.UnhandledException += GlobalUnhandledException;
                TaskScheduler.UnobservedTaskException += UnobservedTaskException;
                _globalExceptionHandlersRegistered = true;
            }

            if (_path is not null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, "app-diagnostics.log");
                if (File.Exists(_path))
                {
                    foreach (var line in File.ReadLines(_path).TakeLast(MaximumRecentEntries))
                    {
                        Recent.Enqueue(line);
                    }
                }
            }
            catch
            {
                // Diagnostics must never prevent the application from starting. Keep the
                // in-memory log available when persistent storage is unavailable.
                _path = null;
            }
        }
        WriteCore("App", $"Session started · {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})", false);
    }

    public static void RecordCrash(Exception exception) =>
        WriteCore("CRASH", exception.ToString(), true);

    public static void RecordCommandFailure(Exception exception) =>
        WriteCore("COMMAND", exception.ToString(), true);

    public static void RecordFailure(string category, Exception exception) =>
        WriteCore(category, exception.ToString(), true);

    private static void GlobalUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            RecordCrash(exception);
        }
    }

    private static void UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args) =>
        RecordCrash(args.Exception);

    public void Write(string category, string message, bool isError = false) =>
        WriteCore(category, message, isError);

    public IReadOnlyList<string> ReadRecent()
    {
        lock (Gate)
        {
            return Recent.ToArray();
        }
    }

    public void Clear()
    {
        lock (Gate)
        {
            Recent.Clear();
            if (_path is not null)
            {
                try
                {
                    File.WriteAllText(_path, string.Empty);
                }
                catch
                {
                    // Clearing diagnostics is best-effort and must not crash Settings.
                }
            }
        }
        RaiseChanged();
    }

    private static void WriteCore(string category, string message, bool isError)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var normalized = message.Replace("\r", string.Empty).Replace("\n", " | ");
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{(isError ? "ERROR" : "INFO")}] [{category}] {normalized}";
        lock (Gate)
        {
            Recent.Enqueue(line);
            while (Recent.Count > MaximumRecentEntries)
            {
                Recent.Dequeue();
            }

            if (_path is not null)
            {
                try
                {
                    RotateIfNeeded(_path);
                    File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // Crash reporting runs from fatal exception callbacks. Never let a storage
                    // problem replace or amplify the original application failure.
                    _path = null;
                }
            }
        }
        RaiseChanged();
    }

    private static void RaiseChanged()
    {
        foreach (EventHandler handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                handler(null, EventArgs.Empty);
            }
            catch
            {
                // A diagnostics observer must not make logging or crash capture fail.
            }
        }
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaximumFileBytes)
        {
            return;
        }

        var previous = path + ".previous";
        File.Move(path, previous, overwrite: true);
    }
}
