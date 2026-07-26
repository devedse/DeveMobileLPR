using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Color = Android.Graphics.Color;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Camera.View;
using DeveMobileLPR.AndroidApp.Camera;
using DeveMobileLPR.AndroidApp.Infrastructure;
using DeveMobileLPR.AndroidApp.Recognition;
using DeveMobileLPR.AndroidApp.UI;
using DeveMobileLPR.Inference;
using DeveMobileLPR.Inference.Onnx;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.AndroidApp;

[Activity(
    Label = "DeveMobileLPR",
    MainLauncher = true,
    Exported = true,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class MainActivity : AppCompatActivity
{
    private const int PermissionRequest = 100;
    private const int RdwPickerRequest = 101;
    private PreviewView _preview = null!;
    private TextView _status = null!;
    private TextView _latestPlate = null!;
    private TextView _metrics = null!;
    private Button _start = null!;
    private Button _stop = null!;
    private CameraXFrameSource? _camera;
    private RecognitionSession? _recognition;
    private SqliteSightingRepository? _repository;
    private AndroidLocationTracker? _location;
    private CancellationTokenSource? _lifetime;
    private bool _initializing;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        BuildInterface();
        EnsurePermissionsAndInitialize();
    }

    private void BuildInterface()
    {
        var root = new FrameLayout(this) { LayoutParameters = MatchParent() };
        _preview = new PreviewView(this) { LayoutParameters = MatchParent() };
        _preview.SetScaleType(PreviewView.ScaleType.FillCenter);
        _preview.SetImplementationMode(PreviewView.ImplementationMode.Performance);
        root.AddView(_preview);
        root.AddView(new RoadGuideView(this), MatchParent());

        _status = Label("Preparing models…", 16, Color.White);
        _status.SetBackgroundColor(Color.Argb(190, 17, 19, 24));
        _status.SetPadding(Dp(16), Dp(10), Dp(16), Dp(10));
        root.AddView(_status, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top));

        var controls = new LinearLayout(this) { Orientation = Orientation.Vertical };
        controls.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        controls.SetBackgroundColor(Color.Argb(220, 17, 19, 24));
        var actions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        actions.SetGravity(GravityFlags.CenterVertical);

        _start = Button("Start", StartClicked);
        _stop = Button("Stop", (_, _) => StopCapture());
        _stop.Enabled = false;
        var history = Button("History", async (_, _) => await ShowHistoryAsync());
        var findPlate = Button("Find plate", (_, _) => ShowPlateSearch());
        var rdw = Button("Import RDW", (_, _) => PickRdwDatabase());
        actions.AddView(_start);
        actions.AddView(_stop);
        actions.AddView(history);
        actions.AddView(findPlate);
        actions.AddView(rdw);

        actions.AddView(Label("  Zoom", 14, Color.LightGray));
        var zoom = new SeekBar(this) { Max = 30, Progress = 10 };
        zoom.ProgressChanged += (_, args) =>
        {
            if (args.FromUser)
            {
                _camera?.SetZoom(Math.Max(1f, args.Progress / 10f));
            }
        };
        actions.AddView(zoom, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        controls.AddView(actions);

        var information = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        information.SetGravity(GravityFlags.CenterVertical);
        _latestPlate = Label("No confirmed plate", 20, Color.Rgb(255, 213, 79));
        _latestPlate.SetPadding(Dp(4), Dp(2), Dp(16), Dp(2));
        information.AddView(_latestPlate, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        _metrics = Label("0 frames", 14, Color.LightGray);
        information.AddView(_metrics);
        controls.AddView(information);

        root.AddView(controls, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom));
        SetContentView(root);
    }

    private void EnsurePermissionsAndInitialize()
    {
        if (CheckSelfPermission(Manifest.Permission.Camera) == Permission.Granted)
        {
            _ = InitializeAsync();
            return;
        }

        RequestPermissions(
            [Manifest.Permission.Camera, Manifest.Permission.AccessCoarseLocation, Manifest.Permission.AccessFineLocation],
            PermissionRequest);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == PermissionRequest && CheckSelfPermission(Manifest.Permission.Camera) == Permission.Granted)
        {
            _ = InitializeAsync();
        }
        else if (requestCode == PermissionRequest)
        {
            SetStatus("Camera permission is required. Grant it in Android settings and reopen the app.", true);
        }
    }

    private async Task InitializeAsync()
    {
        if (_initializing || _recognition is not null)
        {
            return;
        }

        _initializing = true;
        _lifetime = new CancellationTokenSource();
        try
        {
            var files = FilesDir?.AbsolutePath ?? throw new InvalidOperationException("Application files directory is unavailable.");
            SetStatus("Installing and verifying recognition models…");
            var models = await AndroidModelInstaller.EnsureInstalledAsync(
                Assets ?? throw new InvalidOperationException("Application assets are unavailable."),
                files,
                _lifetime.Token);

            SetStatus("Opening local sighting database…");
            _repository = new SqliteSightingRepository(System.IO.Path.Combine(files, "sightings.sqlite"));
            await _repository.InitializeAsync(_lifetime.Token);
            _location = new AndroidLocationTracker(this);
            _location.Start();

            var detector = new OnnxYoloV9PlateDetector(models.Detector, diagnostic: message => RunOnUiThread(() => SetStatus(message)));
            var recognizer = new OnnxCctPlateRecognizer(models.Ocr, diagnostic: message => RunOnUiThread(() => SetStatus(message)));
            var pipeline = new PlateRecognitionPipeline(detector, recognizer);
            var rdwPath = System.IO.Path.Combine(files, RdwDatabaseInstaller.FileName);
            _recognition = new RecognitionSession(pipeline, _repository, new AppVehicleLookup(rdwPath), () => _location.Latest);
            _recognition.Progress += RecognitionProgressed;
            _recognition.PlateConfirmed += PlateConfirmed;
            _recognition.Failed += RecognitionFailed;

            _camera = new CameraXFrameSource(this, this, _preview, frame => _recognition.Submit(frame));
            _camera.Diagnostic += (_, message) => RunOnUiThread(() => SetStatus(message));
            _start.Enabled = true;
            SetStatus("Ready. Position the phone securely, then tap Start before driving.");
        }
        catch (Exception exception)
        {
            SetStatus($"Initialization failed: {exception.Message}", true);
        }
        finally
        {
            _initializing = false;
        }
    }

    private async void StartClicked(object? sender, EventArgs args)
    {
        if (_camera is null)
        {
            return;
        }

        try
        {
            _start.Enabled = false;
            SetStatus("Starting high-resolution camera…");
            await _camera.StartAsync();
            _stop.Enabled = true;
            SetStatus("Scanning. Keep attention on the road; no raw video is stored.");
        }
        catch (Exception exception)
        {
            _start.Enabled = true;
            SetStatus($"Camera failed: {exception.Message}", true);
        }
    }

    private void StopCapture()
    {
        _camera?.Stop();
        _start.Enabled = true;
        _stop.Enabled = false;
        SetStatus("Scanning stopped.");
    }

    private void RecognitionProgressed(object? sender, RecognitionProgress progress) => RunOnUiThread(() =>
    {
        _metrics.Text = $"{progress.ProcessedFrames} frames · {progress.LastProcessingTime.TotalMilliseconds:F0} ms · {progress.Observations.Count} candidates";
    });

    private void PlateConfirmed(object? sender, Sighting sighting) => RunOnUiThread(() =>
    {
        var vehicle = sighting.Vehicle is null ? string.Empty : $" · {sighting.Vehicle.Make} {sighting.Vehicle.Model}";
        _latestPlate.Text = $"{sighting.DisplayPlate}{vehicle}";
    });

    private void RecognitionFailed(object? sender, Exception exception) =>
        RunOnUiThread(() => SetStatus($"Recognition stopped: {exception.Message}", true));

    private async Task ShowHistoryAsync()
    {
        if (_repository is null)
        {
            return;
        }

        try
        {
            var recent = await _repository.GetRecentAsync(50, CancellationToken.None);
            var expensive = await _repository.GetMostExpensiveAsync(CancellationToken.None);
            var lines = new List<string>();
            if (expensive is not null)
            {
                lines.Add($"Most expensive: {expensive.DisplayPlate} · {expensive.Vehicle?.Make} {expensive.Vehicle?.Model} · €{expensive.Vehicle?.CatalogPrice:N0}");
                lines.Add(string.Empty);
            }

            lines.AddRange(recent.Select(FormatSighting));
            var message = lines.Count == 0 ? "No confirmed sightings yet." : string.Join(System.Environment.NewLine, lines);
            var dialog = new AndroidX.AppCompat.App.AlertDialog.Builder(this);
            dialog.SetTitle("Sightings");
            dialog.SetMessage(message);
            dialog.SetPositiveButton("Close", (_, _) => { });
            dialog.Show();
        }
        catch (Exception exception)
        {
            SetStatus($"Could not load history: {exception.Message}", true);
        }
    }

    private void ShowPlateSearch()
    {
        var input = new EditText(this)
        {
            Hint = "AB-12-34",
            InputType = global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextFlagCapCharacters
        };
        input.SetSingleLine(true);
        input.SetPadding(Dp(24), Dp(8), Dp(24), Dp(8));
        var dialog = new AndroidX.AppCompat.App.AlertDialog.Builder(this);
        dialog.SetTitle("Find previous sightings");
        dialog.SetMessage("Enter a plate while parked.");
        dialog.SetView(input);
        dialog.SetNegativeButton("Cancel", (_, _) => { });
        dialog.SetPositiveButton("Search", (_, _) => _ = ShowPlateResultsAsync(input.Text));
        dialog.Show();
    }

    private async Task ShowPlateResultsAsync(string? plate)
    {
        if (_repository is null)
        {
            return;
        }

        try
        {
            var normalized = PlateText.Normalize(plate);
            var rows = await _repository.FindByPlateAsync(normalized, CancellationToken.None);
            var message = rows.Count == 0
                ? $"No confirmed sightings for {normalized}."
                : string.Join(System.Environment.NewLine, rows.Select(FormatSighting));
            var dialog = new AndroidX.AppCompat.App.AlertDialog.Builder(this);
            dialog.SetTitle(PlateText.FormatDutchPlate(normalized));
            dialog.SetMessage(message);
            dialog.SetPositiveButton("Close", (_, _) => { });
            dialog.Show();
        }
        catch (Exception exception)
        {
            SetStatus($"Could not search history: {exception.Message}", true);
        }
    }

    private void PickRdwDatabase()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        // SQLite MIME reporting differs between Android document providers. Accept all
        // openable files here and rely on the strict schema validation after selection.
        intent.SetType("*/*");
        StartActivityForResult(intent, RdwPickerRequest);
    }

#pragma warning disable CS0672
    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
#pragma warning restore CS0672
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != RdwPickerRequest || resultCode != Result.Ok || data?.Data is null)
        {
            return;
        }

        try
        {
            SetStatus("Importing and validating RDW database…");
            await RdwDatabaseInstaller.InstallAsync(this, data.Data, _lifetime?.Token ?? CancellationToken.None);
            SetStatus("RDW database installed. New confirmed plates will include vehicle details.");
        }
        catch (Exception exception)
        {
            SetStatus($"RDW import rejected: {exception.Message}", true);
        }
    }

    protected override void OnDestroy()
    {
        _lifetime?.Cancel();
        _location?.Stop();
        _camera?.Dispose();
        if (_recognition is not null)
        {
            _recognition.Progress -= RecognitionProgressed;
            _recognition.PlateConfirmed -= PlateConfirmed;
            _recognition.Failed -= RecognitionFailed;
            _recognition.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _lifetime?.Dispose();
        base.OnDestroy();
    }

    private void SetStatus(string message, bool error = false)
    {
        _status.Text = message;
        _status.SetTextColor(error ? Color.Rgb(255, 138, 128) : Color.White);
    }

    private static string FormatSighting(Sighting sighting)
    {
        var location = sighting.Location is null ? string.Empty : $" · {sighting.Location.Value.Latitude:F4},{sighting.Location.Value.Longitude:F4}";
        var vehicle = sighting.Vehicle is null ? string.Empty : $" · {sighting.Vehicle.Make} {sighting.Vehicle.Model}";
        return $"{sighting.LastSeenAt.ToLocalTime():g} · {sighting.DisplayPlate}{vehicle}{location}";
    }

    private TextView Label(string text, float size, Color color)
    {
        var view = new TextView(this) { Text = text, TextSize = size };
        view.SetTextColor(color);
        return view;
    }

    private Button Button(string text, EventHandler handler)
    {
        var button = new Button(this) { Text = text };
        button.SetMinimumWidth(Dp(96));
        button.SetMinimumHeight(Dp(56));
        button.Click += handler;
        return button;
    }

    private int Dp(int value) => (int)Math.Round(value * Resources!.DisplayMetrics!.Density);
    private static FrameLayout.LayoutParams MatchParent() => new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
}
