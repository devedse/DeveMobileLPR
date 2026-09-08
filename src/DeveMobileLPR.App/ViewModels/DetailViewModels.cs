using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeveMobileLPR.App.UI;
using DeveMobileLPR.Application;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.App.ViewModels;

internal sealed record SightingCardViewModel(
    long Id,
    string Sequence,
    string Seen,
    string RelativeSeen,
    string Trip,
    string Confidence,
    string LocationLabel,
    GeoPoint? Location,
    ImageSource? SnapshotSource)
{
    public bool HasLocation => Location is not null;
    public bool HasSnapshot => SnapshotSource is not null;
}

internal sealed record TripVehicleCardViewModel(
    string NormalizedPlate,
    string DisplayPlate,
    string VehicleName,
    string Metadata,
    string Price,
    string Seen,
    string Encounters,
    string EarlierSightings,
    string Confidence,
    decimal? CatalogPrice,
    DateTimeOffset FirstSeenAt,
    int SightingCount,
    int EarlierSightingCount,
    GeoPoint? Location,
    ImageSource? SnapshotSource)
{
    public bool HasLocation => Location is not null;
    public bool HasSnapshot => SnapshotSource is not null;
}

internal sealed record HistoryMapSightingViewModel(
    string NormalizedPlate,
    string DisplayPlate,
    string? Price,
    bool IsKnown,
    string Seen,
    float Confidence,
    int ObservationCount,
    GeoPoint Location,
    string VehicleName,
    string? SnapshotPath);

internal sealed record HistoryMapViewModel(
    IReadOnlyList<GeoPoint> Route,
    IReadOnlyList<HistoryMapSightingViewModel> Sightings,
    bool CanOpenVehicleHistory);

internal sealed partial class TripDetailViewModel(
    ISightingRepository repository,
    IVehicleImageStore vehicleImageStore,
    long tripId) : ViewModelBase
{
    internal const string SortByTime = "Most recent";
    internal const string SortByValue = "Highest value";
    internal const string SortBySightings = "Most sightings";

    private bool _isBusy;
    [ObservableProperty]
    private string _title = "Trip";
    [ObservableProperty]
    private string _subtitle = "Loading…";
    [ObservableProperty]
    private string _duration = "—";
    [ObservableProperty]
    private string _distance = "—";
    [ObservableProperty]
    private string _unique = "—";
    [ObservableProperty]
    private string _highlight = "—";
    private string _selectedSort = SortByTime;
    private string _searchText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoute))]
    private IReadOnlyList<TripPoint> _points = [];
    [ObservableProperty]
    private HistoryMapViewModel? _map;
    private IReadOnlyList<TripVehicleCardViewModel> _loadedVehicles = [];

    public ObservableCollection<TripVehicleCardViewModel> Vehicles { get; } = [];
    public bool ShowVehiclesEmpty => !IsBusy && Vehicles.Count == 0;
    public IReadOnlyList<string> SortOptions { get; } = [SortByTime, SortByValue, SortBySightings];
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(ShowVehiclesEmpty));
        }
    }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) ApplySort();
        }
    }
    public string SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (SetProperty(ref _selectedSort, value)) ApplySort();
        }
    }
    public bool HasRoute => Points.Count > 0;
    public GeoPoint? RouteDestination => Points.LastOrDefault()?.Location;

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var tripTask = repository.GetTripAsync(tripId, CancellationToken.None);
            var vehiclesTask = repository.GetVehiclesForTripAsync(tripId, CancellationToken.None);
            var pointsTask = repository.GetTripPointsAsync(tripId, CancellationToken.None);
            var sightingsTask = repository.GetSightingsForTripAsync(tripId, CancellationToken.None);
            await Task.WhenAll(tripTask, vehiclesTask, pointsTask, sightingsTask);
            var trip = await tripTask;
            if (trip is null)
            {
                Subtitle = "This trip no longer exists.";
                return;
            }

            var local = trip.StartedAt.ToLocalTime();
            Title = local.ToString("dddd d MMMM");
            Subtitle = $"Started at {local:HH:mm}";
            Duration = DisplayFormat.Duration(trip.Duration);
            Distance = DisplayFormat.Distance(trip.DistanceMeters);
            Unique = trip.UniqueVehicleCount.ToString();
            Highlight = trip.MostExpensiveCatalogPrice is null ? "No RDW value" : $"{DisplayFormat.CompactPrice(trip.MostExpensiveCatalogPrice)} · {trip.MostExpensiveDisplayPlate}";
            Points = await pointsTask;
            var vehicleSummaries = await vehiclesTask;
            Map = new HistoryMapViewModel(
                Points.Select(point => point.Location).ToArray(),
                CreateMapSightings(await sightingsTask, vehicleSummaries),
                true);
            _loadedVehicles = vehicleSummaries.Select(CreateVehicle).ToArray();
            ApplySort();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private IReadOnlyList<HistoryMapSightingViewModel> CreateMapSightings(
        IReadOnlyList<Sighting> sightings,
        IReadOnlyList<TripVehicleSummary> vehicles)
    {
        var knownPlates = vehicles
            .Where(vehicle => vehicle.EarlierSightingCount > 0)
            .Select(vehicle => vehicle.NormalizedPlate)
            .ToHashSet(StringComparer.Ordinal);

        return sightings
            .GroupBy(sighting => sighting.NormalizedPlate, StringComparer.Ordinal)
            .Select(group => group.OrderBy(sighting => sighting.FirstSeenAt).First())
            .Where(sighting => sighting.Location is not null)
            .OrderBy(sighting => sighting.FirstSeenAt)
            .Select(sighting =>
            {
                var vehicleName = string.Join(' ', new[] { sighting.Vehicle?.Make, sighting.Vehicle?.Model }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                return new HistoryMapSightingViewModel(
                    sighting.NormalizedPlate,
                    sighting.DisplayPlate,
                    sighting.Vehicle?.CatalogPrice is { } price ? DisplayFormat.CompactPrice(price) : null,
                    knownPlates.Contains(sighting.NormalizedPlate),
                    $"First spotted {sighting.FirstSeenAt.ToLocalTime():HH:mm}",
                    sighting.Confidence,
                    sighting.ObservationCount,
                    sighting.Location!.Value,
                    string.IsNullOrWhiteSpace(vehicleName) ? "Vehicle details unavailable" : vehicleName,
                    vehicleImageStore.ResolvePath(sighting.SnapshotReference));
            })
            .ToArray();
    }

    private TripVehicleCardViewModel CreateVehicle(TripVehicleSummary vehicle)
    {
        var vehicleName = string.Join(' ', new[] { vehicle.Vehicle?.Make, vehicle.Vehicle?.Model }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var metadata = string.Join(" · ", new[] { vehicle.Vehicle?.RegistrationYear?.ToString(), vehicle.Vehicle?.FuelDescription, vehicle.Vehicle?.BodyType }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var local = vehicle.FirstSeenAt.ToLocalTime();
        return new TripVehicleCardViewModel(
            vehicle.NormalizedPlate,
            vehicle.DisplayPlate,
            string.IsNullOrWhiteSpace(vehicleName) ? "Vehicle details unavailable" : vehicleName,
            string.IsNullOrWhiteSpace(metadata) ? "No RDW specifications" : metadata,
            DisplayFormat.Price(vehicle.Vehicle?.CatalogPrice),
            $"First seen at {local:HH:mm}",
            vehicle.SightingCount == 1 ? "1 encounter this trip" : $"{vehicle.SightingCount} encounters this trip",
            vehicle.EarlierSightingCount switch
            {
                0 => "First time seen",
                1 => "1 earlier sighting",
                _ => $"{vehicle.EarlierSightingCount} earlier sightings"
            },
            $"{vehicle.Confidence:P0} · {vehicle.ObservationCount} reads",
            vehicle.Vehicle?.CatalogPrice,
            vehicle.FirstSeenAt,
            vehicle.SightingCount,
            vehicle.EarlierSightingCount,
            vehicle.LastLocation,
            SnapshotImageSource.Create(vehicleImageStore, vehicle.SnapshotReference));
    }

    private void ApplySort()
    {
        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? _loadedVehicles
            : _loadedVehicles.Where(vehicle =>
                vehicle.DisplayPlate.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || vehicle.VehicleName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || vehicle.Metadata.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        var ordered = (SelectedSort switch
        {
            SortByValue => filtered.OrderByDescending(vehicle => vehicle.CatalogPrice ?? decimal.MinValue).ThenByDescending(vehicle => vehicle.FirstSeenAt),
            SortBySightings => filtered.OrderByDescending(vehicle => vehicle.SightingCount).ThenByDescending(vehicle => vehicle.FirstSeenAt),
            _ => filtered.OrderByDescending(vehicle => vehicle.FirstSeenAt)
        }).ToArray();

        Vehicles.Clear();
        for (var targetIndex = 0; targetIndex < ordered.Length; targetIndex++)
        {
            Vehicles.Add(ordered[targetIndex]);
        }
        OnPropertyChanged(nameof(ShowVehiclesEmpty));
    }
}

internal sealed partial class VehicleDetailViewModel(
    ISightingRepository repository,
    IVehicleImageStore vehicleImageStore,
    string normalizedPlate) : ViewModelBase
{
    [ObservableProperty]
    private bool _isBusy;
    [ObservableProperty]
    private string _displayPlate = PlateText.FormatDutchPlate(normalizedPlate);
    [ObservableProperty]
    private string _vehicleName = "Vehicle details unavailable";
    [ObservableProperty]
    private string _metadata = "Import RDW for specifications";
    [ObservableProperty]
    private string _price = "Unknown value";
    [ObservableProperty]
    private string _appearances = "0";
    [ObservableProperty]
    private string _trips = "0";
    [ObservableProperty]
    private string _firstSeen = "—";
    [ObservableProperty]
    private string _lastSeen = "—";
    [ObservableProperty]
    private string _locationSummary = "No locations recorded";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocations))]
    private IReadOnlyList<Sighting> _locationSightings = [];
    [ObservableProperty]
    private HistoryMapViewModel? _map;

    public ObservableCollection<SightingCardViewModel> Sightings { get; } = [];
    public bool HasLocations => LocationSightings.Count > 0;

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var results = await repository.FindByPlateAsync(normalizedPlate, CancellationToken.None);
            var tripTasks = results
                .Where(result => result.TripId is not null)
                .Select(result => result.TripId!.Value)
                .Distinct()
                .ToDictionary(id => id, id => repository.GetTripAsync(id, CancellationToken.None));
            await Task.WhenAll(tripTasks.Values);
            var trips = new Dictionary<long, TripSummary>();
            foreach (var (id, task) in tripTasks)
            {
                if (await task is { } trip) trips.Add(id, trip);
            }
            var chronological = results.OrderBy(result => result.FirstSeenAt).ToArray();
            var sequenceById = chronological.Select((sighting, index) => (sighting.Id, Sequence: index + 1)).ToDictionary(item => item.Id, item => item.Sequence);
            Sightings.Clear();
            foreach (var result in results)
            {
                var local = result.LastSeenAt.ToLocalTime();
                var sequence = sequenceById[result.Id];
                var trip = result.TripId is { } tripId && trips.TryGetValue(tripId, out var summary)
                    ? $"Trip on {summary.StartedAt.ToLocalTime():ddd d MMM} · started {summary.StartedAt.ToLocalTime():HH:mm}"
                    : "Outside a saved trip";
                var locationLabel = result.Location is { } location
                    ? $"{location.Latitude:F5}, {location.Longitude:F5}" + (location.AccuracyMeters is { } accuracy ? $" · ±{accuracy:F0} m" : string.Empty)
                    : "Location unavailable";
                Sightings.Add(new SightingCardViewModel(
                    result.Id,
                    sequence == 1 ? "First sighting" : $"Sighting {sequence} of {results.Count}",
                    $"{local:ddd d MMM yyyy · HH:mm}",
                    DisplayFormat.Relative(result.LastSeenAt),
                    trip,
                    $"{result.Confidence:P0} · {result.ObservationCount} reads",
                    locationLabel,
                    result.Location,
                    SnapshotImageSource.Create(vehicleImageStore, result.SnapshotReference)));
            }
            if (results.Count == 0) return;
            var latest = results[0];
            DisplayPlate = latest.DisplayPlate;
            VehicleName = string.Join(' ', new[] { latest.Vehicle?.Make, latest.Vehicle?.Model }.Where(value => !string.IsNullOrWhiteSpace(value))) is { Length: > 0 } name ? name : "Vehicle details unavailable";
            Metadata = string.Join(" · ", new[] { latest.Vehicle?.RegistrationYear?.ToString(), latest.Vehicle?.FuelDescription, latest.Vehicle?.BodyType }.Where(value => !string.IsNullOrWhiteSpace(value))) is { Length: > 0 } metadata ? metadata : "No RDW specifications";
            Price = DisplayFormat.Price(results.Select(item => item.Vehicle?.CatalogPrice).Where(value => value is not null).Max());
            Appearances = results.Count.ToString();
            Trips = results.Where(result => result.TripId is not null).Select(result => result.TripId).Distinct().Count().ToString();
            FirstSeen = DisplayFormat.Relative(results.MinBy(item => item.FirstSeenAt)!.FirstSeenAt);
            LastSeen = DisplayFormat.Relative(results.MaxBy(item => item.LastSeenAt)!.LastSeenAt);
            LocationSightings = chronological.Where(result => result.Location is not null).ToArray();
            Map = CreateMap(chronological);
            var distinctLocations = LocationSightings
                .Select(result => result.Location!.Value)
                .Select(location => (Math.Round(location.Latitude, 5), Math.Round(location.Longitude, 5)))
                .Distinct()
                .Count();
            LocationSummary = LocationSightings.Count == 0
                ? "No locations recorded"
                : $"{LocationSightings.Count} {(LocationSightings.Count == 1 ? "sighting" : "sightings")} at {distinctLocations} recorded {(distinctLocations == 1 ? "location" : "locations")}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private HistoryMapViewModel CreateMap(IReadOnlyList<Sighting> sightings) => new(
        [],
        sightings
            .Select((sighting, index) => (Sighting: sighting, IsKnown: index > 0))
            .Where(item => item.Sighting.Location is not null)
            .Select(item =>
            {
                var sighting = item.Sighting;
                var vehicleName = string.Join(' ', new[] { sighting.Vehicle?.Make, sighting.Vehicle?.Model }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                return new HistoryMapSightingViewModel(
                    sighting.NormalizedPlate,
                    sighting.DisplayPlate,
                    sighting.Vehicle?.CatalogPrice is { } price ? DisplayFormat.CompactPrice(price) : null,
                    item.IsKnown,
                    $"Seen {sighting.FirstSeenAt.ToLocalTime():ddd d MMM yyyy · HH:mm}",
                    sighting.Confidence,
                    sighting.ObservationCount,
                    sighting.Location!.Value,
                    string.IsNullOrWhiteSpace(vehicleName) ? "Vehicle details unavailable" : vehicleName,
                    vehicleImageStore.ResolvePath(sighting.SnapshotReference));
            }).ToArray(),
        false);
}
