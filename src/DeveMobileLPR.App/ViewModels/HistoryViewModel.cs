using System.Collections.ObjectModel;
using System.Windows.Input;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;
using DeveMobileLPR.App.UI;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.ViewModels;

internal sealed class TripCardViewModel(
    long id,
    string day,
    string time,
    string duration,
    string distance,
    string vehicleCount,
    string sightingCount,
    string highlight,
    string highlightPlate,
    Action<TripCardViewModel, bool> selectionChanged) : ViewModelBase
{
    private bool _isSelected;
    private bool _isSelectionMode;
    public long Id { get; } = id;
    public string Day { get; } = day;
    public string Time { get; } = time;
    public string Duration { get; } = duration;
    public string Distance { get; } = distance;
    public string VehicleCount { get; } = vehicleCount;
    public string SightingCount { get; } = sightingCount;
    public string Highlight { get; } = highlight;
    public string HighlightPlate { get; } = highlightPlate;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value)) selectionChanged(this, value);
        }
    }
    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set => SetProperty(ref _isSelectionMode, value);
    }
}

internal sealed record VehicleCardViewModel(
    string NormalizedPlate,
    string DisplayPlate,
    string VehicleName,
    string Metadata,
    string Price,
    string Seen,
    string History,
    bool HasLocation,
    ImageSource? SnapshotSource)
{
    public bool HasSnapshot => SnapshotSource is not null;
}

internal enum HistorySection
{
    Dashboard,
    Trips,
    Vehicles
}

internal sealed class HistoryViewModel : ViewModelBase
{
    private const int PageSize = 50;
    private const string AllTime = "All time";
    private const string AnyValue = "Any value";
    private const string MostRecent = "Most recent";
    private readonly DriveCoordinator _coordinator;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private bool _isBusy;
    private HistorySection _selectedSection = HistorySection.Dashboard;
    private string _searchText = string.Empty;
    private string _todayTrips = "0";
    private string _todayUnique = "0";
    private string _todayDistance = "0 km";
    private string _todayTopValue = "—";
    private string _todayTopPlate = "No valued car yet";
    private string _selectedPeriod = AllTime;
    private string _selectedMinimumValue = AnyValue;
    private string _selectedVehicleSort = MostRecent;
    private bool _hasMoreTrips;
    private bool _hasMoreVehicles;
    private bool _isTripSelectionMode;
    private bool _suppressTripSelectionCallback;
    private int _vehicleQueryVersion;
    private CancellationTokenSource? _searchCancellation;

    public HistoryViewModel(DriveCoordinator coordinator)
    {
        _coordinator = coordinator;
        ShowDashboardCommand = new Command(() => SelectSection(HistorySection.Dashboard));
        ShowTripsCommand = new Command(() => SelectSection(HistorySection.Trips));
        ShowVehiclesCommand = new Command(() => SelectSection(HistorySection.Vehicles));
        ResetVehicleFiltersCommand = new Command(ResetVehicleFilters);
        RefreshCommand = new AsyncCommand(LoadAsync);
        LoadMoreTripsCommand = new AsyncCommand(LoadMoreTripsAsync, () => _hasMoreTrips);
        LoadMoreVehiclesCommand = new AsyncCommand(LoadMoreVehiclesAsync, () => _hasMoreVehicles);
    }

    public ObservableCollection<TripCardViewModel> Trips { get; } = [];
    internal DriveCoordinator Coordinator => _coordinator;
    public ObservableCollection<VehicleCardViewModel> Vehicles { get; } = [];
    public ICommand ShowDashboardCommand { get; }
    public ICommand ShowTripsCommand { get; }
    public ICommand ShowVehiclesCommand { get; }
    public ICommand ResetVehicleFiltersCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand LoadMoreTripsCommand { get; }
    public AsyncCommand LoadMoreVehiclesCommand { get; }
    public IReadOnlyList<string> PeriodOptions { get; } = ["Last 24 hours", "Last 7 days", "Last 30 days", "Last 90 days", AllTime];
    public IReadOnlyList<string> MinimumValueOptions { get; } = [AnyValue, "Over €50k", "Over €100k", "Over €300k", "Over €500k", "Over €1m"];
    public IReadOnlyList<string> VehicleSortOptions { get; } = [MostRecent, "Highest value"];
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) NotifyEmptyStates();
        }
    }
    public bool ShowDashboard => _selectedSection == HistorySection.Dashboard;
    public bool ShowTrips => _selectedSection == HistorySection.Trips;
    public bool ShowVehicles => _selectedSection == HistorySection.Vehicles;
    public bool ShowTripsEmpty => ShowTrips && !IsBusy && Trips.Count == 0;
    public bool ShowVehiclesEmpty => ShowVehicles && !IsBusy && Vehicles.Count == 0;
    public bool IsTripSelectionMode
    {
        get => _isTripSelectionMode;
        private set
        {
            if (SetProperty(ref _isTripSelectionMode, value))
            {
                foreach (var trip in Trips) trip.IsSelectionMode = value;
            }
        }
    }
    public int SelectedTripCount => Trips.Count(trip => trip.IsSelected);
    public bool CanRemoveTrips => SelectedTripCount > 0;
    public string RemoveTripsText => $"Remove {SelectedTripCount} {(SelectedTripCount == 1 ? "trip" : "trips")}";
    public string TodayTrips { get => _todayTrips; private set => SetProperty(ref _todayTrips, value); }
    public string TodayUnique { get => _todayUnique; private set => SetProperty(ref _todayUnique, value); }
    public string TodayDistance { get => _todayDistance; private set => SetProperty(ref _todayDistance, value); }
    public string TodayTopValue { get => _todayTopValue; private set => SetProperty(ref _todayTopValue, value); }
    public string TodayTopPlate { get => _todayTopPlate; private set => SetProperty(ref _todayTopPlate, value); }

    public string SelectedPeriod
    {
        get => _selectedPeriod;
        set { if (SetProperty(ref _selectedPeriod, value)) _ = ReloadVehiclesAfterDelayAsync(); }
    }

    public string SelectedMinimumValue
    {
        get => _selectedMinimumValue;
        set { if (SetProperty(ref _selectedMinimumValue, value)) _ = ReloadVehiclesAfterDelayAsync(); }
    }

    public string SelectedVehicleSort
    {
        get => _selectedVehicleSort;
        set { if (SetProperty(ref _selectedVehicleSort, value)) _ = ReloadVehiclesAfterDelayAsync(); }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _ = ReloadVehiclesAfterDelayAsync();
            }
        }
    }

    public async Task LoadAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            IsBusy = true;
            await _coordinator.InitializeAsync();
            var repository = _coordinator.Repository;
            var localStart = new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
            var todayTask = repository.GetStatisticsAsync(localStart.ToUniversalTime(), localStart.AddDays(1).ToUniversalTime(), CancellationToken.None);
            var tripsTask = repository.GetTripsAsync(0, PageSize, CancellationToken.None);
            var vehiclesTask = repository.GetVehicleHistoryAsync(CreateVehicleQuery(0), CancellationToken.None);
            await Task.WhenAll(todayTask, tripsTask, vehiclesTask);

            var today = todayTask.Result;
            TodayTrips = today.TripCount.ToString();
            TodayUnique = today.UniqueVehicleCount.ToString();
            TodayDistance = DisplayFormat.Distance(today.DistanceMeters);
            TodayTopValue = DisplayFormat.CompactPrice(today.MostExpensiveSighting?.Vehicle?.CatalogPrice);
            TodayTopPlate = today.MostExpensiveSighting?.DisplayPlate ?? "No valued car yet";

            ClearTripSelection();
            Trips.Clear();
            AppendTrips(tripsTask.Result);
            SetHasMoreTrips(tripsTask.Result.Count == PageSize);

            ReplaceVehicles(vehiclesTask.Result);
            SetHasMoreVehicles(vehiclesTask.Result.Count == PageSize);
            NotifyEmptyStates();
        }
        finally
        {
            IsBusy = false;
            _loadGate.Release();
        }
    }

    private async Task ReloadVehiclesAfterDelayAsync()
    {
        var queryVersion = Interlocked.Increment(ref _vehicleQueryVersion);
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        try
        {
            await Task.Delay(250, token);
            var results = await _coordinator.Repository.GetVehicleHistoryAsync(CreateVehicleQuery(0), token);
            if (!token.IsCancellationRequested && queryVersion == Volatile.Read(ref _vehicleQueryVersion))
            {
                ReplaceVehicles(results);
                SetHasMoreVehicles(results.Count == PageSize);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private VehicleHistoryQuery CreateVehicleQuery(int offset)
    {
        var seenSince = SelectedPeriod switch
        {
            "Last 24 hours" => DateTimeOffset.UtcNow.AddDays(-1),
            "Last 7 days" => DateTimeOffset.UtcNow.AddDays(-7),
            "Last 30 days" => DateTimeOffset.UtcNow.AddDays(-30),
            "Last 90 days" => DateTimeOffset.UtcNow.AddDays(-90),
            _ => (DateTimeOffset?)null
        };
        var minimumValue = SelectedMinimumValue switch
        {
            "Over €50k" => 50_000m,
            "Over €100k" => 100_000m,
            "Over €300k" => 300_000m,
            "Over €500k" => 500_000m,
            "Over €1m" => 1_000_000m,
            _ => (decimal?)null
        };
        var sort = SelectedVehicleSort == "Highest value" ? VehicleHistorySort.HighestValue : VehicleHistorySort.MostRecent;
        return new VehicleHistoryQuery(SearchText, seenSince, minimumValue, sort, offset, PageSize);
    }

    private async Task LoadMoreTripsAsync()
    {
        if (!_hasMoreTrips) return;
        var results = await _coordinator.Repository.GetTripsAsync(Trips.Count, PageSize, CancellationToken.None);
        AppendTrips(results);
        SetHasMoreTrips(results.Count == PageSize);
    }

    private async Task LoadMoreVehiclesAsync()
    {
        if (!_hasMoreVehicles) return;
        var queryVersion = Volatile.Read(ref _vehicleQueryVersion);
        var token = _searchCancellation?.Token ?? CancellationToken.None;
        try
        {
            var results = await _coordinator.Repository.GetVehicleHistoryAsync(CreateVehicleQuery(Vehicles.Count), token);
            if (!token.IsCancellationRequested && queryVersion == Volatile.Read(ref _vehicleQueryVersion))
            {
                AppendVehicles(results);
                SetHasMoreVehicles(results.Count == PageSize);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void AppendTrips(IEnumerable<TripSummary> trips)
    {
        foreach (var trip in trips)
        {
            Trips.Add(new TripCardViewModel(
                trip.Id,
                trip.StartedAt.ToLocalTime().ToString("ddd d MMM"),
                trip.StartedAt.ToLocalTime().ToString("HH:mm"),
                DisplayFormat.Duration(trip.Duration),
                DisplayFormat.Distance(trip.DistanceMeters),
                $"{trip.UniqueVehicleCount} unique",
                $"{trip.SightingCount} confirmed",
                DisplayFormat.CompactPrice(trip.MostExpensiveCatalogPrice),
                trip.MostExpensiveDisplayPlate ?? "No RDW value",
                TripSelectionChanged));
        }
    }

    public void BeginTripSelection(TripCardViewModel trip)
    {
        IsTripSelectionMode = true;
        trip.IsSelected = true;
    }

    public void ToggleTripSelection(TripCardViewModel trip)
    {
        if (IsTripSelectionMode) trip.IsSelected = !trip.IsSelected;
    }

    public void ClearTripSelection()
    {
        _suppressTripSelectionCallback = true;
        try
        {
            foreach (var trip in Trips)
            {
                trip.IsSelected = false;
                trip.IsSelectionMode = false;
            }
        }
        finally
        {
            _suppressTripSelectionCallback = false;
        }
        IsTripSelectionMode = false;
        NotifyTripSelectionChanged();
    }

    public async Task<DeletedTrips> DeleteSelectedTripsAsync()
    {
        var ids = Trips.Where(trip => trip.IsSelected).Select(trip => trip.Id).ToArray();
        if (ids.Length == 0) return new DeletedTrips(0, 0, []);
        var deleted = await _coordinator.DeleteTripsAsync(ids);
        await LoadAsync();
        return deleted;
    }

    private void TripSelectionChanged(TripCardViewModel trip, bool selected)
    {
        if (_suppressTripSelectionCallback) return;
        if (selected && !IsTripSelectionMode) IsTripSelectionMode = true;
        NotifyTripSelectionChanged();
    }

    private void NotifyTripSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedTripCount));
        OnPropertyChanged(nameof(CanRemoveTrips));
        OnPropertyChanged(nameof(RemoveTripsText));
    }

    private void SetHasMoreTrips(bool value)
    {
        _hasMoreTrips = value;
        LoadMoreTripsCommand.RaiseCanExecuteChanged();
    }

    private void SetHasMoreVehicles(bool value)
    {
        _hasMoreVehicles = value;
        LoadMoreVehiclesCommand.RaiseCanExecuteChanged();
    }

    private void ResetVehicleFilters()
    {
        SearchText = string.Empty;
        SelectedPeriod = AllTime;
        SelectedMinimumValue = AnyValue;
        SelectedVehicleSort = MostRecent;
    }

    private void SelectSection(HistorySection section)
    {
        if (_selectedSection == section)
        {
            return;
        }

        ClearTripSelection();
        _selectedSection = section;
        OnPropertyChanged(nameof(ShowDashboard));
        OnPropertyChanged(nameof(ShowTrips));
        OnPropertyChanged(nameof(ShowVehicles));
        NotifyEmptyStates();
    }

    private void ReplaceVehicles(IReadOnlyList<VehicleHistorySummary> results)
    {
        Vehicles.Clear();
        AppendVehicles(results);
        NotifyEmptyStates();
    }

    private void AppendVehicles(IEnumerable<VehicleHistorySummary> results)
    {
        foreach (var vehicle in results)
        {
            var name = string.Join(' ', new[] { vehicle.Vehicle?.Make, vehicle.Vehicle?.Model }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var metadata = string.Join(" · ", new[]
            {
                vehicle.Vehicle?.RegistrationYear?.ToString(),
                vehicle.Vehicle?.FuelDescription,
                vehicle.Vehicle?.BodyType
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            Vehicles.Add(new VehicleCardViewModel(
                vehicle.NormalizedPlate,
                vehicle.DisplayPlate,
                string.IsNullOrWhiteSpace(name) ? "Vehicle details unavailable" : name,
                string.IsNullOrWhiteSpace(metadata) ? "Import RDW for specifications" : metadata,
                DisplayFormat.Price(vehicle.Vehicle?.CatalogPrice),
                DisplayFormat.Relative(vehicle.LastSeenAt),
                $"{FormatCount(vehicle.SightingCount, "sighting")} · {FormatCount(vehicle.TripCount, "trip")}",
                vehicle.LastLocation is not null,
                SnapshotImageSource.Create(_coordinator.VehicleImageStore, vehicle.SnapshotReference)));
        }
    }

    private static string FormatCount(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private void NotifyEmptyStates()
    {
        OnPropertyChanged(nameof(ShowTripsEmpty));
        OnPropertyChanged(nameof(ShowVehiclesEmpty));
    }
}
