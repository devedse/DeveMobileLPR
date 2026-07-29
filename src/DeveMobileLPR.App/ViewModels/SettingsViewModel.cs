using DeveMobileLPR.App.Services;
using DeveMobileLPR.App.UI;

namespace DeveMobileLPR.App.ViewModels;

internal sealed record RecognitionFrameRateOption(
    string Name,
    string Detail,
    int MaximumFramesPerSecond)
{
    public override string ToString() => Name;
}

internal sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly DriveCoordinator _coordinator;
    private readonly RdwDatabaseService _rdw;
    private readonly HistoryExportService _export;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private string _rdwTitle = "RDW data not installed";
    private string _rdwDetail = "Import the generated rdw.sqlite file to add make, model, value, fuel, year, and body type.";
    private string _historyDetail = "Loading local history…";
    private string _permissionsDetail = "Checking Android permissions…";
    private RecognitionFrameRateOption _selectedRecognitionFrameRate;

    public SettingsViewModel(AppSettings settings, DriveCoordinator coordinator, RdwDatabaseService rdw, HistoryExportService export)
    {
        _settings = settings;
        _coordinator = coordinator;
        _rdw = rdw;
        _export = export;
        RecognitionFrameRateOptions =
        [
            new("2 FPS", "Battery saver · suitable when heat and power use matter most", 2),
            new("4 FPS", "Balanced · the previous default recognition cadence", 4),
            new("8 FPS", "Responsive · checks twice as many frames as the balanced mode", 8),
            new("12 FPS", "High · more CPU/GPU use for fast-moving traffic", 12),
            new("Unlimited", "Maximum throughput · submits every available analysis frame and drops stale queued frames", 0)
        ];
        _selectedRecognitionFrameRate = RecognitionFrameRateOptions.FirstOrDefault(
                option => option.MaximumFramesPerSecond == _settings.RecognitionFramesPerSecond)
            ?? RecognitionFrameRateOptions[1];
    }

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public string RdwTitle { get => _rdwTitle; private set => SetProperty(ref _rdwTitle, value); }
    public string RdwDetail { get => _rdwDetail; private set => SetProperty(ref _rdwDetail, value); }
    public Color RdwColor => _rdw.IsInstalled ? Color.FromArgb("#58E0C2") : Color.FromArgb("#F5C542");
    public string HistoryDetail { get => _historyDetail; private set => SetProperty(ref _historyDetail, value); }
    public string PermissionsDetail { get => _permissionsDetail; private set => SetProperty(ref _permissionsDetail, value); }
    public string Version => $"DeveMobileLPR {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";
    public IReadOnlyList<RecognitionFrameRateOption> RecognitionFrameRateOptions { get; }

    public RecognitionFrameRateOption SelectedRecognitionFrameRate
    {
        get => _selectedRecognitionFrameRate;
        set
        {
            if (SetProperty(ref _selectedRecognitionFrameRate, value))
            {
                _settings.RecognitionFramesPerSecond = value.MaximumFramesPerSecond;
                OnPropertyChanged(nameof(RecognitionFrameRateDetail));
            }
        }
    }

    public string RecognitionFrameRateDetail => SelectedRecognitionFrameRate.Detail;

    public bool TrackLocation
    {
        get => _settings.TrackLocation;
        set { if (_settings.TrackLocation != value) { _settings.TrackLocation = value; OnPropertyChanged(); _coordinator.RefreshSettings(); } }
    }

    public bool ShowRoadGuide
    {
        get => _settings.ShowRoadGuide;
        set { if (_settings.ShowRoadGuide != value) { _settings.ShowRoadGuide = value; OnPropertyChanged(); _coordinator.RefreshSettings(); } }
    }

    public bool ConfirmationHaptic
    {
        get => _settings.ConfirmationHaptic;
        set { if (_settings.ConfirmationHaptic != value) { _settings.ConfirmationHaptic = value; OnPropertyChanged(); } }
    }

    public async Task RefreshAsync()
    {
        await _coordinator.InitializeAsync();
        RefreshRdw();
        var stats = await _coordinator.Repository.GetStatisticsAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
        HistoryDetail = $"{stats.TripCount} trips · {stats.SightingCount} sightings · {stats.UniqueVehicleCount} unique cars · {DisplayFormat.Distance(stats.DistanceMeters)}";
        var camera = await Permissions.CheckStatusAsync<Permissions.Camera>();
        var location = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        PermissionsDetail = $"Camera: {PermissionName(camera)} · Location: {PermissionName(location)}";
    }

    public async Task ImportRdwAsync(FileResult file)
    {
        IsBusy = true;
        StatusMessage = "Importing and validating the RDW snapshot…";
        OnPropertyChanged(nameof(HasStatus));
        try
        {
            await using var stream = await file.OpenReadAsync();
            await _rdw.ImportAsync(stream, CancellationToken.None);
            StatusMessage = "RDW installed. New confirmations now include vehicle details.";
            RefreshRdw();
        }
        catch (Exception exception)
        {
            StatusMessage = $"RDW import rejected: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    public async Task<string> CreateExportAsync()
    {
        IsBusy = true;
        StatusMessage = "Creating a private CSV export…";
        OnPropertyChanged(nameof(HasStatus));
        try
        {
            var path = await _export.CreateCsvAsync(CancellationToken.None);
            StatusMessage = "Export created.";
            return path;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    public async Task DeleteHistoryAsync()
    {
        IsBusy = true;
        try
        {
            await _coordinator.DeleteHistoryAsync();
            StatusMessage = "Trip and sighting history deleted. The RDW database was kept.";
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    private void RefreshRdw()
    {
        if (_rdw.IsInstalled)
        {
            RdwTitle = "RDW vehicle data installed";
            RdwDetail = $"{FormatBytes(_rdw.SizeBytes)} · updated {_rdw.UpdatedAt?.ToLocalTime():g}";
        }
        else
        {
            RdwTitle = "RDW data not installed";
            RdwDetail = "Import rdw.sqlite to add make, model, value, fuel, year, and body type.";
        }
        OnPropertyChanged(nameof(RdwColor));
    }

    private static string PermissionName(PermissionStatus status) => status switch { PermissionStatus.Granted => "allowed", PermissionStatus.Denied => "not allowed", _ => "not requested" };
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} GB" : $"{bytes / (1024d * 1024):0} MB";
}
