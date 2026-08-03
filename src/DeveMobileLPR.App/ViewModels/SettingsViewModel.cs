using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;
using DeveMobileLPR.App.UI;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.ViewModels;

internal sealed record RecognitionFrameRateOption(
    string Name,
    string Detail,
    int MaximumFramesPerSecond)
{
    public override string ToString() => Name;
}

internal sealed record RecognitionTuningValue(
    string Name,
    string Value,
    string Description);

internal sealed record RecognitionTuningSection(
    string Name,
    string Description,
    IReadOnlyList<RecognitionTuningValue> Values);

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

    public SettingsViewModel(
        AppSettings settings,
        DriveCoordinator coordinator,
        RdwDatabaseService rdw,
        HistoryExportService export,
        RecognitionTuningConfiguration recognitionTuning)
    {
        _settings = settings;
        _coordinator = coordinator;
        _rdw = rdw;
        _export = export;
        recognitionTuning.Validate();
        RecognitionTuningSections = CreateRecognitionTuningSections(recognitionTuning);
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
    public IReadOnlyList<RecognitionTuningSection> RecognitionTuningSections { get; }

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

    public bool RecognitionDebugEnabled
    {
        get => _settings.RecognitionDebugEnabled;
        set
        {
            if (_settings.RecognitionDebugEnabled != value)
            {
                _settings.RecognitionDebugEnabled = value;
                OnPropertyChanged();
                _coordinator.RefreshSettings();
            }
        }
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

    private static IReadOnlyList<RecognitionTuningSection> CreateRecognitionTuningSections(
        RecognitionTuningConfiguration tuning)
    {
        var road = tuning.Detector_RoadRegion;
        return
        [
            new(
                "Detector & frame pipeline",
                "Controls which model detections reach OCR and how much work one analyzed frame may perform.",
                [
                    Value("Minimum detector confidence", Percentage(tuning.Detector_ConfidenceThreshold), "Lower-scoring plate boxes are ignored."),
                    Value("Road region (L / T / R / B)", $"{Percentage(road.Left)} / {Percentage(road.Top)} / {Percentage(road.Right)} / {Percentage(road.Bottom)}", "Normalized part of the source image sent to the detector."),
                    Value("Minimum detected plate", $"{tuning.Detector_MinimumPlateWidthPixels:0.#} × {tuning.Detector_MinimumPlateHeightPixels:0.#} px", "Smaller detector boxes are discarded."),
                    Value("Duplicate-box overlap", Percentage(tuning.Detector_NonMaximumSuppressionIntersectionOverUnionThreshold), "A lower-confidence box is removed when it overlaps a stronger box by more than this IoU."),
                    Value("Maximum detections", $"{tuning.Detector_MaximumDetectionsPerFrame} / frame", "Safety limit applied after duplicate boxes are removed."),
                    Value("Maximum OCR attempts", $"{tuning.Detector_MaximumOcrAttemptsPerFrame} / frame", "Highest-confidence boxes are read first.")
                ]),
            new(
                "Crop quality",
                "Scores plate crops for sharpness, exposure, and usable pixel size before temporal consensus.",
                [
                    Value("Minimum crop", $"{tuning.CropQuality_MinimumCropWidthPixels:0.#} × {tuning.CropQuality_MinimumCropHeightPixels:0.#} px", "Smaller crops receive a zero quality score."),
                    Value("Sampling grid", $"{tuning.CropQuality_SampleColumns} × {tuning.CropQuality_SampleRows}", "Luminance samples used to estimate crop quality."),
                    Value("Sharpness normalization", tuning.CropQuality_SharpnessNormalization.ToString("0.#"), "Scales edge strength into a 0–100% score."),
                    Value("Target luminance", $"{tuning.CropQuality_TargetLuminance:0.#} / 255", "Brightness that receives the best exposure score."),
                    Value("Exposure range", $"{tuning.CropQuality_ExposureRange:0.#} levels", "Brightness distance over which the exposure score falls to zero."),
                    Value("Full-size plate width", $"{tuning.CropQuality_FullSizeWidthPixels:0.#} px", "Crop width that receives the full size score."),
                    Value("Quality weights", $"sharp {Percentage(tuning.CropQuality_SharpnessWeight)} · exposure {Percentage(tuning.CropQuality_ExposureWeight)} · size {Percentage(tuning.CropQuality_SizeWeight)}", "The three contributions always add up to 100%."),
                    Value("Minimum non-zero score", Percentage(tuning.CropQuality_MinimumScore), "Floor used for a crop that passes the minimum dimensions.")
                ]),
            new(
                "Tracking & association gates",
                "Determines whether observations on different AI frames belong to the same physical plate.",
                [
                    Value("Track timeout", $"{tuning.Tracking_TrackTimeout.TotalMilliseconds:0} ms", "A track expires after this time without a matching observation."),
                    Value("Stored observations", tuning.Tracking_MaximumObservationsPerTrack.ToString(), "Maximum recent reads retained per track."),
                    Value("Previous-box overlap", Percentage(tuning.Tracking_MinimumPreviousIntersectionOverUnion), "Minimum IoU accepted by the motion association fallback."),
                    Value("Exact-text center distance", Percentage(tuning.Tracking_MaximumExactTextCenterDistanceFraction), "Maximum center movement as a fraction of the frame diagonal."),
                    Value("Similar-text center distance", Percentage(tuning.Tracking_MaximumSimilarTextCenterDistanceFraction), "Stricter center movement allowed for a one-character OCR difference."),
                    Value("Exact / similar scale ratio", $"{tuning.Tracking_MaximumExactOrSimilarScaleRatio:0.##}×", "Maximum plate size change for text-led association."),
                    Value("Motion scale ratio", $"{tuning.Tracking_MaximumMotionScaleRatio:0.##}×", "Maximum plate size change for prediction-led association."),
                    Value("Predicted-box overlap", Percentage(tuning.Tracking_MinimumPredictedIntersectionOverUnion), "Minimum predicted IoU accepted by motion association."),
                    Value("Predicted center distance", $"{tuning.Tracking_MaximumPredictedCenterDistanceInPlateWidths:0.##} plate widths", "Maximum miss distance from the predicted plate center."),
                    Value("Prediction horizon", $"{tuning.Tracking_MaximumPredictionSteps:0.##} frame intervals", "Caps how far velocity may be extrapolated."),
                    Value("Partial text length", $"{tuning.Tracking_MinimumPartialTextLength} characters", "Shortest matching prefix or suffix allowed by motion association."),
                    Value("Predicted size range", $"{tuning.Tracking_PredictionMinimumScale:0.##}× – {tuning.Tracking_PredictionMaximumScale:0.##}×", "Limits extrapolated box growth or shrinkage.")
                ]),
            new(
                "Association ranking",
                "Ranks eligible track/observation pairs when several assignments are possible.",
                [
                    Value("Distance weight", Percentage(tuning.AssociationScore_DistanceWeight), "Rewards closeness to the expected center."),
                    Value("Scale weight", Percentage(tuning.AssociationScore_ScaleWeight), "Rewards similar plate size."),
                    Value("Overlap weight", Percentage(tuning.AssociationScore_OverlapWeight), "Rewards overlap with the previous or predicted box.")
                ]),
            new(
                "Normal confirmation",
                "Combines several reads before a plate is saved as a confirmed sighting.",
                [
                    Value("Required observations", tuning.Consensus_MinimumObservations.ToString(), "Minimum agreeing evidence for normal confirmation."),
                    Value("Accepted text length", $"{tuning.Consensus_MinimumPlateLength}–{tuning.Consensus_MaximumPlateLength} characters", "Normalized OCR texts outside this range are ignored."),
                    Value("Supporting edit distance", $"≤ {tuning.Consensus_MaximumSupportingEditDistance}", "Maximum character edits for a read to support per-character consensus."),
                    Value("Winner share", Percentage(tuning.Consensus_MinimumWinnerShare), "Minimum share of weighted evidence for the winning text."),
                    Value("Winner margin", Percentage(tuning.Consensus_MinimumWinnerMargin), "Minimum lead over the runner-up text."),
                    Value("Per-character confidence", Percentage(tuning.Consensus_MinimumCharacterConfidence), "Every reconstructed character must reach this confidence."),
                    Value("Quality contribution floor", Percentage(tuning.Consensus_MinimumQualityWeight), "Prevents a weak crop from reducing evidence weight all the way to zero."),
                    Value("Dutch format check", Enabled(tuning.Consensus_RequirePlausibleDutchFormatForDutchRegion), "Rejects implausible Dutch layouts when OCR identifies the Netherlands.")
                ]),
            new(
                "Strong short-lived confirmation",
                "A deliberately strict fast path for plates that leave view before normal consensus at a phone-like AI rate.",
                [
                    Value("Fast path", Enabled(tuning.StrongPair_Enabled), "Allows exceptionally strong identical reads to confirm early."),
                    Value("Distinct frames", tuning.StrongPair_RequiredDistinctFrames.ToString(), "Required number of separate AI frames with exactly the same text."),
                    Value("OCR confidence", Percentage(tuning.StrongPair_MinimumOcrConfidence), "Minimum whole-read OCR confidence on every frame."),
                    Value("Crop quality", Percentage(tuning.StrongPair_MinimumQuality), "Minimum crop quality on every frame."),
                    Value("Combined evidence", Percentage(tuning.StrongPair_MinimumEvidenceWeight), "Minimum detector × OCR × quality evidence on every frame."),
                    Value("Character probability", Percentage(tuning.StrongPair_MinimumCharacterProbability), "Minimum probability for every selected character."),
                    Value("Character margin", Percentage(tuning.StrongPair_MinimumCharacterMargin), "Minimum lead of each character over its next-best alternative."),
                    Value("Dutch format check", Enabled(tuning.StrongPair_RequirePlausibleDutchFormat), "Requires a plausible Dutch registration layout.")
                ]),
            new(
                "Confirmation correction",
                "Allows a live track to replace an early wrong plate only when later evidence is materially stronger.",
                [
                    Value("Additional observations", tuning.ConfirmationCorrection_MinimumAdditionalObservations.ToString(), "Minimum supporting-frame gain over the previous confirmation."),
                    Value("Minimum confidence", Percentage(tuning.ConfirmationCorrection_MinimumConfidence), "Minimum confidence required for the replacement consensus.")
                ])
        ];
    }

    private static RecognitionTuningValue Value(string name, string value, string description) =>
        new(name, value, description);

    private static string Percentage(float value) => value.ToString("P0");
    private static string Enabled(bool value) => value ? "Enabled" : "Disabled";
}
