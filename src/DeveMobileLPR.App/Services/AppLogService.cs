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

    public static event EventHandler? Changed;

    public static void Initialize(string directory)
    {
        lock (Gate)
        {
            if (_path is not null)
            {
                return;
            }

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
        WriteCore("App", $"Session started · {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})", false);
    }

    public static void RecordCrash(Exception exception) =>
        WriteCore("CRASH", exception.ToString(), true);

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
                File.WriteAllText(_path, string.Empty);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
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
                RotateIfNeeded(_path);
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        Changed?.Invoke(null, EventArgs.Empty);
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
