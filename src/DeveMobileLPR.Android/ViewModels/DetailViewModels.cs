using System.Collections.ObjectModel;
using DeveMobileLPR.AndroidApp.UI;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.AndroidApp.ViewModels;

internal sealed record SightingCardViewModel(
    long Id,
    string DisplayPlate,
    string VehicleName,
    string Metadata,
    string Price,
    string Seen,
    string Confidence,
    GeoPoint? Location)
{
    public bool HasLocation => Location is not null;
}

internal sealed class TripDetailViewModel(SqliteSightingRepository repository, long tripId) : ViewModelBase
{
    private bool _isBusy;
    private string _title = "Trip";
    private string _subtitle = "Loading…";
    private string _duration = "—";
    private string _distance = "—";
    private string _unique = "—";
    private string _highlight = "—";
    private IReadOnlyList<TripPoint> _points = [];

    public ObservableCollection<SightingCardViewModel> Sightings { get; } = [];
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }
    public string Duration { get => _duration; private set => SetProperty(ref _duration, value); }
    public string Distance { get => _distance; private set => SetProperty(ref _distance, value); }
    public string Unique { get => _unique; private set => SetProperty(ref _unique, value); }
    public string Highlight { get => _highlight; private set => SetProperty(ref _highlight, value); }
    public IReadOnlyList<TripPoint> Points { get => _points; private set { if (SetProperty(ref _points, value)) { OnPropertyChanged(nameof(HasRoute)); } } }
    public bool HasRoute => Points.Count > 0;
    public GeoPoint? RouteDestination => Points.LastOrDefault()?.Location;

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var tripTask = repository.GetTripAsync(tripId, CancellationToken.None);
            var sightingsTask = repository.GetSightingsForTripAsync(tripId, CancellationToken.None);
            var pointsTask = repository.GetTripPointsAsync(tripId, CancellationToken.None);
            await Task.WhenAll(tripTask, sightingsTask, pointsTask);
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
            Sightings.Clear();
            foreach (var sighting in sightingsTask.Result)
            {
                Sightings.Add(CreateSighting(sighting));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static SightingCardViewModel CreateSighting(Sighting sighting)
    {
        var vehicleName = string.Join(' ', new[] { sighting.Vehicle?.Make, sighting.Vehicle?.Model }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var metadata = string.Join(" · ", new[] { sighting.Vehicle?.RegistrationYear?.ToString(), sighting.Vehicle?.FuelDescription, sighting.Vehicle?.BodyType }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new SightingCardViewModel(
            sighting.Id,
            sighting.DisplayPlate,
            string.IsNullOrWhiteSpace(vehicleName) ? "Vehicle details unavailable" : vehicleName,
            string.IsNullOrWhiteSpace(metadata) ? "No RDW specifications" : metadata,
            DisplayFormat.Price(sighting.Vehicle?.CatalogPrice),
            DisplayFormat.Relative(sighting.LastSeenAt),
            $"{sighting.Confidence:P0} · {sighting.ObservationCount} reads",
            sighting.Location);
    }
}

internal sealed class VehicleDetailViewModel(SqliteSightingRepository repository, string normalizedPlate) : ViewModelBase
{
    private bool _isBusy;
    private string _displayPlate = PlateText.FormatDutchPlate(normalizedPlate);
    private string _vehicleName = "Vehicle details unavailable";
    private string _metadata = "Import RDW for specifications";
    private string _price = "Unknown value";
    private string _appearances = "0";
    private string _firstSeen = "—";
    private string _lastSeen = "—";

    public ObservableCollection<SightingCardViewModel> Sightings { get; } = [];
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string DisplayPlate { get => _displayPlate; private set => SetProperty(ref _displayPlate, value); }
    public string VehicleName { get => _vehicleName; private set => SetProperty(ref _vehicleName, value); }
    public string Metadata { get => _metadata; private set => SetProperty(ref _metadata, value); }
    public string Price { get => _price; private set => SetProperty(ref _price, value); }
    public string Appearances { get => _appearances; private set => SetProperty(ref _appearances, value); }
    public string FirstSeen { get => _firstSeen; private set => SetProperty(ref _firstSeen, value); }
    public string LastSeen { get => _lastSeen; private set => SetProperty(ref _lastSeen, value); }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var results = await repository.FindByPlateAsync(normalizedPlate, CancellationToken.None);
            Sightings.Clear();
            foreach (var result in results) Sightings.Add(TripDetailViewModel.CreateSighting(result));
            if (results.Count == 0) return;
            var latest = results[0];
            DisplayPlate = latest.DisplayPlate;
            VehicleName = string.Join(' ', new[] { latest.Vehicle?.Make, latest.Vehicle?.Model }.Where(value => !string.IsNullOrWhiteSpace(value))) is { Length: > 0 } name ? name : "Vehicle details unavailable";
            Metadata = string.Join(" · ", new[] { latest.Vehicle?.RegistrationYear?.ToString(), latest.Vehicle?.FuelDescription, latest.Vehicle?.BodyType }.Where(value => !string.IsNullOrWhiteSpace(value))) is { Length: > 0 } metadata ? metadata : "No RDW specifications";
            Price = DisplayFormat.Price(results.Select(item => item.Vehicle?.CatalogPrice).Where(value => value is not null).Max());
            Appearances = results.Count.ToString();
            FirstSeen = DisplayFormat.Relative(results.MinBy(item => item.FirstSeenAt)!.FirstSeenAt);
            LastSeen = DisplayFormat.Relative(results.MaxBy(item => item.LastSeenAt)!.LastSeenAt);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
