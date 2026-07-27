using System.Collections.ObjectModel;
using System.Windows.Input;
using DeveMobileLPR.AndroidApp.Services;
using DeveMobileLPR.AndroidApp.UI;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.AndroidApp.ViewModels;

internal sealed record TripCardViewModel(
    long Id,
    string Day,
    string Time,
    string Duration,
    string Distance,
    string VehicleCount,
    string SightingCount,
    string Highlight,
    string HighlightPlate);

internal sealed record VehicleCardViewModel(
    string NormalizedPlate,
    string DisplayPlate,
    string VehicleName,
    string Metadata,
    string Price,
    string Seen,
    string History,
    bool HasLocation);

internal sealed class HistoryViewModel : ViewModelBase
{
    private readonly DriveCoordinator _coordinator;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private bool _isBusy;
    private bool _showTrips = true;
    private string _searchText = string.Empty;
    private string _todayTrips = "0";
    private string _todayUnique = "0";
    private string _todayDistance = "0 km";
    private string _todayTopValue = "—";
    private string _todayTopPlate = "No valued car yet";
    private CancellationTokenSource? _searchCancellation;

    public HistoryViewModel(DriveCoordinator coordinator)
    {
        _coordinator = coordinator;
        ShowTripsCommand = new Command(() => ShowTrips = true);
        ShowVehiclesCommand = new Command(() => ShowTrips = false);
        RefreshCommand = new AsyncCommand(LoadAsync);
    }

    public ObservableCollection<TripCardViewModel> Trips { get; } = [];
    internal DriveCoordinator Coordinator => _coordinator;
    public ObservableCollection<VehicleCardViewModel> Vehicles { get; } = [];
    public ICommand ShowTripsCommand { get; }
    public ICommand ShowVehiclesCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool ShowTrips { get => _showTrips; private set { if (SetProperty(ref _showTrips, value)) { OnPropertyChanged(nameof(ShowVehicles)); OnPropertyChanged(nameof(EmptyMessage)); } } }
    public bool ShowVehicles => !ShowTrips;
    public string EmptyMessage => ShowTrips ? "Your completed drives will appear here." : "Confirmed vehicles will appear here.";
    public bool HasTrips => Trips.Count > 0;
    public bool HasVehicles => Vehicles.Count > 0;
    public bool ShowTripsEmpty => ShowTrips && !HasTrips && !IsBusy;
    public bool ShowVehiclesEmpty => ShowVehicles && !HasVehicles && !IsBusy;
    public string TodayTrips { get => _todayTrips; private set => SetProperty(ref _todayTrips, value); }
    public string TodayUnique { get => _todayUnique; private set => SetProperty(ref _todayUnique, value); }
    public string TodayDistance { get => _todayDistance; private set => SetProperty(ref _todayDistance, value); }
    public string TodayTopValue { get => _todayTopValue; private set => SetProperty(ref _todayTopValue, value); }
    public string TodayTopPlate { get => _todayTopPlate; private set => SetProperty(ref _todayTopPlate, value); }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _ = SearchAfterDelayAsync();
            }
        }
    }

    public async Task LoadAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            IsBusy = true;
            NotifyEmptyStates();
            await _coordinator.InitializeAsync();
            var repository = _coordinator.Repository;
            var localStart = new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
            var todayTask = repository.GetStatisticsAsync(localStart.ToUniversalTime(), localStart.AddDays(1).ToUniversalTime(), CancellationToken.None);
            var tripsTask = repository.GetTripsAsync(250, CancellationToken.None);
            var vehiclesTask = repository.GetVehicleHistoryAsync(SearchText, 500, CancellationToken.None);
            await Task.WhenAll(todayTask, tripsTask, vehiclesTask);

            var today = todayTask.Result;
            TodayTrips = today.TripCount.ToString();
            TodayUnique = today.UniqueVehicleCount.ToString();
            TodayDistance = DisplayFormat.Distance(today.DistanceMeters);
            TodayTopValue = DisplayFormat.CompactPrice(today.MostExpensiveSighting?.Vehicle?.CatalogPrice);
            TodayTopPlate = today.MostExpensiveSighting?.DisplayPlate ?? "No valued car yet";

            Trips.Clear();
            foreach (var trip in tripsTask.Result)
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
                    trip.MostExpensiveDisplayPlate ?? "No RDW value"));
            }

            ReplaceVehicles(vehiclesTask.Result);
        }
        finally
        {
            IsBusy = false;
            NotifyEmptyStates();
            _loadGate.Release();
        }
    }

    private async Task SearchAfterDelayAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        try
        {
            await Task.Delay(250, token);
            var results = await _coordinator.Repository.GetVehicleHistoryAsync(SearchText, 500, token);
            if (!token.IsCancellationRequested)
            {
                ReplaceVehicles(results);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ReplaceVehicles(IReadOnlyList<VehicleHistorySummary> results)
    {
        Vehicles.Clear();
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
                vehicle.LastLocation is not null));
        }
        NotifyEmptyStates();
    }

    private void NotifyEmptyStates()
    {
        OnPropertyChanged(nameof(HasTrips));
        OnPropertyChanged(nameof(HasVehicles));
        OnPropertyChanged(nameof(ShowTripsEmpty));
        OnPropertyChanged(nameof(ShowVehiclesEmpty));
    }

    private static string FormatCount(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
