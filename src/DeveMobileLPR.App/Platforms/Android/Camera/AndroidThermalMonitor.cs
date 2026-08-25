using Android.Content;
using Android.OS;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class AndroidThermalMonitor : IDisposable
{
    private readonly Context _context;
    private readonly PowerManager? _powerManager;
    private readonly Timer _timer;
    private AndroidThermalSnapshot _current = AndroidThermalSnapshot.Unknown;
    private bool _disposed;

    public AndroidThermalMonitor(Context context)
    {
        _context = context.ApplicationContext ?? context;
        _powerManager = _context.GetSystemService(Context.PowerService) as PowerManager;
        _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    public event Action<AndroidThermalSnapshot>? Changed;

    public AndroidThermalSnapshot Current => Volatile.Read(ref _current);

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var status = OperatingSystem.IsAndroidVersionAtLeast(29) && _powerManager is not null
                ? (int)_powerManager.CurrentThermalStatus
                : 0;
            double? batteryTemperature = null;
            using var filter = new IntentFilter(Intent.ActionBatteryChanged);
            using var battery = _context.RegisterReceiver(null, filter);
            var tenthsCelsius = battery?.GetIntExtra(BatteryManager.ExtraTemperature, int.MinValue)
                ?? int.MinValue;
            if (tenthsCelsius != int.MinValue && tenthsCelsius > -1000)
            {
                batteryTemperature = tenthsCelsius / 10d;
            }

            var next = new AndroidThermalSnapshot(status, batteryTemperature);
            var previous = Interlocked.Exchange(ref _current, next);
            if (next != previous)
            {
                Changed?.Invoke(next);
            }
        }
        catch
        {
            // Thermal telemetry must never interfere with camera capture. The next poll retries.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}

internal sealed record AndroidThermalSnapshot(int Status, double? BatteryTemperatureCelsius)
{
    private const int ModerateStatus = 2;

    public static AndroidThermalSnapshot Unknown { get; } = new(0, null);

    public bool IsTooHotForCameraRetry => Status >= ModerateStatus;

    public string DisplayText => BatteryTemperatureCelsius is { } temperature
        ? $"THERMAL {StatusLabel} · BATTERY {temperature:0.0}°C"
        : $"THERMAL {StatusLabel}";

    public string StatusLabel => Status switch
    {
        0 => "NORMAL",
        1 => "LIGHT",
        2 => "MODERATE",
        3 => "SEVERE",
        4 => "CRITICAL",
        5 => "EMERGENCY",
        6 => "SHUTDOWN",
        _ => $"LEVEL {Status}"
    };
}
