using System.Collections.ObjectModel;
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
    int EarlierSightingCount,
    GeoPoint? Location,
    ImageSource? SnapshotSource)
{
    public bool HasLocation => Location is not null;
    public bool HasSnapshot => SnapshotSource is not null;
}

internal sealed record TripMapSightingViewModel(
    string NormalizedPlate,
    string DisplayPlate,
    string? Price,
    bool IsKnown,
    DateTimeOffset FirstSeenAt,
    float Confidence,
    int ObservationCount,
    GeoPoint Location,
    string VehicleName,
    string? SnapshotPath);

internal sealed record TripMapViewModel(
    IReadOnlyList<TripPoint> Route,
    IReadOnlyList<TripMapSightingViewModel> Sightings);

internal sealed class TripDetailViewModel(
    ISightingRepository repository,
    IVehicleImageStore vehicleImageStore,
    long tripId) : ViewModelBase
{
    internal const string SortByTime = "Time seen";
    internal const string SortByValue = "Highest value";
    internal const string SortByEarlierSightings = "Most seen before";

    private bool _isBusy;
    private string _title = "Trip";
    private string _subtitle = "Loading…";
    private string _duration = "—";
    private string _distance = "—";
    private string _unique = "—";
    private string _highlight = "—";
    private string _selectedSort = SortByTime;
    private IReadOnlyList<TripPoint> _points = [];
    private TripMapViewModel? _map;
    private IReadOnlyList<TripVehicleCardViewModel> _loadedVehicles = [];

    public ObservableCollection<TripVehicleCardViewModel> Vehicles { get; } = [];
    public IReadOnlyList<string> SortOptions { get; } = [SortByTime, SortByValue, SortByEarlierSightings];
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }
    public string Duration { get => _duration; private set => SetProperty(ref _duration, value); }
    public string Distance { get => _distance; private set => SetProperty(ref _distance, value); }
    public string Unique { get => _unique; private set => SetProperty(ref _unique, value); }
    public string Highlight { get => _highlight; private set => SetProperty(ref _highlight, value); }
    public string SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (SetProperty(ref _selectedSort, value)) ApplySort();
        }
    }
    public IReadOnlyList<TripPoint> Points { get => _points; private set { if (SetProperty(ref _points, value)) { OnPropertyChanged(nameof(HasRoute)); } } }
    public TripMapViewModel? Map { get => _map; private set => SetProperty(ref _map, value); }
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
            var trip = tripTask.Result;
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
            Points = pointsTask.Result;
            var vehicleSummaries = vehiclesTask.Result;
            Map = new TripMapViewModel(Points, CreateMapSightings(sightingsTask.Result, vehicleSummaries));
            _loadedVehicles = vehicleSummaries.Select(CreateVehicle).ToArray();
            ApplySort();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private IReadOnlyList<TripMapSightingViewModel> CreateMapSightings(
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
                return new TripMapSightingViewModel(
                    sighting.NormalizedPlate,
                    sighting.DisplayPlate,
                    sighting.Vehicle?.CatalogPrice is { } price ? DisplayFormat.CompactPrice(price) : null,
                    knownPlates.Contains(sighting.NormalizedPlate),
                    sighting.FirstSeenAt,
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
            vehicle.EarlierSightingCount,
            vehicle.LastLocation,
            SnapshotImageSource.Create(vehicleImageStore, vehicle.SnapshotReference));
    }

    private void ApplySort()
    {
        var ordered = (SelectedSort switch
        {
            SortByValue => _loadedVehicles.OrderByDescending(vehicle => vehicle.CatalogPrice ?? decimal.MinValue).ThenBy(vehicle => vehicle.FirstSeenAt),
            SortByEarlierSightings => _loadedVehicles.OrderByDescending(vehicle => vehicle.EarlierSightingCount).ThenBy(vehicle => vehicle.FirstSeenAt),
            _ => _loadedVehicles.OrderBy(vehicle => vehicle.FirstSeenAt)
        }).ToArray();

        for (var targetIndex = 0; targetIndex < ordered.Length; targetIndex++)
        {
            var currentIndex = Vehicles.IndexOf(ordered[targetIndex]);
            if (currentIndex < 0)
            {
                Vehicles.Insert(targetIndex, ordered[targetIndex]);
            }
            else if (currentIndex != targetIndex)
            {
                Vehicles.Move(currentIndex, targetIndex);
            }
        }
    }
}

internal sealed class VehicleDetailViewModel(
    ISightingRepository repository,
    IVehicleImageStore vehicleImageStore,
    string normalizedPlate) : ViewModelBase
{
    private bool _isBusy;
    private string _displayPlate = PlateText.FormatDutchPlate(normalizedPlate);
    private string _vehicleName = "Vehicle details unavailable";
    private string _metadata = "Import RDW for specifications";
    private string _price = "Unknown value";
    private string _appearances = "0";
    private string _trips = "0";
    private string _firstSeen = "—";
    private string _lastSeen = "—";
    private string _locationSummary = "No locations recorded";
    private IReadOnlyList<Sighting> _locationSightings = [];

    public ObservableCollection<SightingCardViewModel> Sightings { get; } = [];
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string DisplayPlate { get => _displayPlate; private set => SetProperty(ref _displayPlate, value); }
    public string VehicleName { get => _vehicleName; private set => SetProperty(ref _vehicleName, value); }
    public string Metadata { get => _metadata; private set => SetProperty(ref _metadata, value); }
    public string Price { get => _price; private set => SetProperty(ref _price, value); }
    public string Appearances { get => _appearances; private set => SetProperty(ref _appearances, value); }
    public string Trips { get => _trips; private set => SetProperty(ref _trips, value); }
    public string FirstSeen { get => _firstSeen; private set => SetProperty(ref _firstSeen, value); }
    public string LastSeen { get => _lastSeen; private set => SetProperty(ref _lastSeen, value); }
    public string LocationSummary { get => _locationSummary; private set => SetProperty(ref _locationSummary, value); }
    public IReadOnlyList<Sighting> LocationSightings
    {
        get => _locationSightings;
        private set
        {
            if (SetProperty(ref _locationSightings, value)) OnPropertyChanged(nameof(HasLocations));
        }
    }
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
}
