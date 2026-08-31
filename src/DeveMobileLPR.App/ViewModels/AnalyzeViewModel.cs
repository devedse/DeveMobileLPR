using System.Collections.ObjectModel;
using System.Windows.Input;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Application;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.App.ViewModels;

internal sealed record FrameSamplingOption(string Name, string Detail, int? Interval)
{
    public override string ToString() => Name;
}

internal sealed class AnalysisListItem : ViewModelBase
{
    private string _detail;
    private double _progress;

    public AnalysisListItem(
        string title,
        string detail,
        string sourceStatus,
        bool isProcessing,
        ICommand? openCommand,
        ICommand? deleteCommand = null)
    {
        Title = title;
        _detail = detail;
        SourceStatus = sourceStatus;
        IsProcessing = isProcessing;
        OpenCommand = openCommand;
        DeleteCommand = deleteCommand;
    }

    public string Title { get; }
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
    public string SourceStatus { get; }
    public bool IsProcessing { get; }
    public bool CanDelete => DeleteCommand is not null;
    public ICommand? OpenCommand { get; }
    public ICommand? DeleteCommand { get; }
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }
}
internal sealed record AnalyzedPlateIndexItem(string DisplayPlate, string Detail, ICommand OpenCommand);

internal sealed class AnalyzeViewModel : ViewModelBase
{
    private const int MaximumPreviewCacheEntries = 8;
    private readonly VideoAnalysisService _analysis;
    private readonly JsonVideoAnalysisRepository _repository;
    private readonly AppSettings _settings;
    private readonly AsyncCommand _processCommand;
    private readonly AsyncCommand _previousFrameCommand;
    private readonly AsyncCommand _nextFrameCommand;
    private readonly Command _cancelCommand;
    private readonly Dictionary<long, byte[]> _previewCache = [];
    private readonly Queue<long> _previewCacheOrder = [];
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _previewCancellation;
    private IReadOnlyList<VideoAnalysisResult> _savedResults = [];
    private AnalysisListItem? _processingItem;
    private VideoAnalysisResult? _result;
    private string? _stagedPath;
    private string _selectedFileName = "No video selected";
    private FrameSamplingOption _selectedSampling;
    private bool _isProcessing;
    private bool _isReviewing;
    private double _progress;
    private string _progressText = string.Empty;
    private string _statusMessage = "Select a video to create a private, on-device analysis run.";
    private int _currentFrameIndex;
    private ImageSource? _currentPreview;
    private AnalyzedVideoFrame? _currentFrame;
    private string _currentFrameTitle = string.Empty;
    private string _currentFrameDetail = string.Empty;
    private double _currentPositionFraction;
    private IReadOnlyList<double> _detectionMarkers = [];
    private IReadOnlyList<double> _framePositions = [];
    private bool _initialized;
    private int _customSamplingInterval = 15;
    private bool _limitToFirstThirtySeconds;
    private RecognitionStreamDiagnostics? _processingDiagnostics;

    public AnalyzeViewModel(
        VideoAnalysisService analysis,
        JsonVideoAnalysisRepository repository,
        AppSettings settings)
    {
        _analysis = analysis;
        _repository = repository;
        _settings = settings;
        SamplingOptions =
        [
            new("All frames", "Process every reported source frame", 1),
            new("Every 2nd frame", "Process half of the source frames", 2),
            new("Every 4th frame", "Balanced analysis for most recordings", 4),
            new("Every 8th frame", "Faster exploratory pass", 8),
            new("Every 15th frame", "Equivalent to about two analyzed frames per second for a 30 FPS source", 15),
            new("Custom interval", "Choose exactly how many source frames to skip between recognition runs", null)
        ];
        _selectedSampling = SamplingOptions[2];
        _processCommand = new AsyncCommand(ProcessAsync, () => HasSelectedFile && !IsProcessing);
        _previousFrameCommand = new AsyncCommand(PreviousFrameAsync, () => IsReviewing && _currentFrameIndex > 0);
        _nextFrameCommand = new AsyncCommand(NextFrameAsync, () => IsReviewing && _result is not null && _currentFrameIndex < _result.Frames.Count - 1);
        _cancelCommand = new Command(Cancel, () => IsProcessing);
        CloseReviewCommand = new Command(CloseReview);
    }

    public IReadOnlyList<FrameSamplingOption> SamplingOptions { get; }
    public ObservableCollection<string> CurrentReads { get; } = [];
    public ObservableCollection<AnalysisListItem> Analyses { get; } = [];
    public ObservableCollection<AnalyzedPlateIndexItem> DetectedPlates { get; } = [];
    public ICommand ProcessCommand => _processCommand;
    public ICommand PreviousFrameCommand => _previousFrameCommand;
    public ICommand NextFrameCommand => _nextFrameCommand;
    public ICommand CancelCommand => _cancelCommand;
    public ICommand CloseReviewCommand { get; }
    public string SelectedFileName { get => _selectedFileName; private set => SetProperty(ref _selectedFileName, value); }
    public bool HasSelectedFile => _stagedPath is not null;
    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                OnPropertyChanged(nameof(CanSelectVideo));
                OnPropertyChanged(nameof(ShowProcessingDiagnostics));
                RefreshCommands();
            }
        }
    }
    public bool CanSelectVideo => !IsProcessing;
    public bool IsReviewing { get => _isReviewing; private set { if (SetProperty(ref _isReviewing, value)) RefreshCommands(); } }
    public bool ShowSetup => !IsReviewing;
    public bool HasAnalyses => Analyses.Count > 0;
    public bool HasDetectedPlates => DetectedPlates.Count > 0;
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public RecognitionStreamDiagnostics? ProcessingDiagnostics { get => _processingDiagnostics; private set => SetProperty(ref _processingDiagnostics, value); }
    public ImageSource? CurrentPreview { get => _currentPreview; private set => SetProperty(ref _currentPreview, value); }
    public AnalyzedVideoFrame? CurrentFrame
    {
        get => _currentFrame;
        private set
        {
            if (SetProperty(ref _currentFrame, value))
            {
                OnPropertyChanged(nameof(ShowCurrentDiagnostics));
                OnPropertyChanged(nameof(CurrentOverlays));
            }
        }
    }

    /// <summary>The reviewed frame's detections in the same overlay model the live drive view uses.</summary>
    public IReadOnlyList<DriveOverlay> CurrentOverlays => CurrentFrame is null
        ? []
        : DriveOverlayFactory.CreateAnalyzedFrameOverlays(CurrentFrame, RecognitionDebugEnabled);
    public string CurrentFrameTitle { get => _currentFrameTitle; private set => SetProperty(ref _currentFrameTitle, value); }
    public string CurrentFrameDetail { get => _currentFrameDetail; private set => SetProperty(ref _currentFrameDetail, value); }
    public double CurrentPositionFraction { get => _currentPositionFraction; set => SetProperty(ref _currentPositionFraction, value); }
    public IReadOnlyList<double> DetectionMarkers { get => _detectionMarkers; private set => SetProperty(ref _detectionMarkers, value); }
    public IReadOnlyList<double> FramePositions { get => _framePositions; private set => SetProperty(ref _framePositions, value); }
    public bool RecognitionDebugEnabled => _settings.TrackingDiagnosticsEnabled;
    public bool ShowProcessingDiagnostics => IsProcessing && RecognitionDebugEnabled;
    public bool ShowCurrentDiagnostics => RecognitionDebugEnabled && CurrentFrame?.Diagnostics is not null;
    public bool UsesCustomSampling => SelectedSampling.Interval is null;
    public string SelectedSamplingDetail => SelectedSampling.Detail;

    public int CustomSamplingInterval
    {
        get => _customSamplingInterval;
        set
        {
            if (SetProperty(ref _customSamplingInterval, Math.Clamp(value, 1, 10_000)))
            {
                _processCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool LimitToFirstThirtySeconds
    {
        get => _limitToFirstThirtySeconds;
        set => SetProperty(ref _limitToFirstThirtySeconds, value);
    }

    public FrameSamplingOption SelectedSampling
    {
        get => _selectedSampling;
        set
        {
            if (SetProperty(ref _selectedSampling, value))
            {
                OnPropertyChanged(nameof(UsesCustomSampling));
                OnPropertyChanged(nameof(SelectedSamplingDetail));
            }
        }
    }

    public void RefreshSettings()
    {
        OnPropertyChanged(nameof(RecognitionDebugEnabled));
        OnPropertyChanged(nameof(ShowProcessingDiagnostics));
        OnPropertyChanged(nameof(ShowCurrentDiagnostics));
        OnPropertyChanged(nameof(CurrentOverlays));
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        _savedResults = await _repository.LoadAllAsync(CancellationToken.None);
        RebuildAnalyses();
    }

    public async Task SelectFileAsync(FileResult file)
    {
        if (IsProcessing)
        {
            return;
        }

        StatusMessage = "Preparing the selected video…";
        var previousPath = _stagedPath;
        try
        {
            _stagedPath = await _analysis.StageAsync(
                new SelectedVideoFile(
                    file.FileName,
                    file.FullPath,
                    _ => file.OpenReadAsync()),
                CancellationToken.None);
            SelectedFileName = file.FileName;
            ClearResult();
            StatusMessage = "Ready to process. The video stays on this device.";
            OnPropertyChanged(nameof(HasSelectedFile));
            _processCommand.RaiseCanExecuteChanged();
            if (previousPath is not null
                && !_savedResults.Any(result => string.Equals(result.SourcePath, previousPath, StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(previousPath);
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not open the video: {exception.Message}";
        }
    }

    public void CloseReview()
    {
        _runCancellation?.Cancel();
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        IsReviewing = false;
        CurrentPreview = null;
        OnPropertyChanged(nameof(ShowSetup));
    }

    private async Task ProcessAsync()
    {
        if (_stagedPath is null)
        {
            return;
        }

        var sourcePath = _stagedPath;
        var displayName = SelectedFileName;
        var sampling = SelectedSampling;
        var samplingInterval = sampling.Interval ?? CustomSamplingInterval;
        var samplingName = sampling.Interval is null ? $"Every {samplingInterval}th frame" : sampling.Name;
        var options = new VideoAnalysisOptions(
            new VideoFrameSampling(samplingInterval),
            LimitToFirstThirtySeconds ? TimeSpan.FromSeconds(30) : null,
            RecognitionDebugEnabled);
        ClearResult();
        _stagedPath = null;
        SelectedFileName = "No video selected";
        OnPropertyChanged(nameof(HasSelectedFile));
        IsProcessing = true;
        ProgressText = "Preparing models…";
        StatusMessage = "Processing full-resolution frames on this device.";
        var processingItem = new AnalysisListItem(displayName, ProgressText, samplingName, true, null);
        _processingItem = processingItem;
        RebuildAnalyses();
        _runCancellation = new CancellationTokenSource();
        var progress = new Progress<VideoAnalysisProgress>(update =>
        {
            Progress = update.Fraction;
            ProgressText = $"{update.Fraction:P0} · {update.ProcessedFrames:N0} of {update.TotalFrames:N0} frames · total {update.AverageTotalMilliseconds:F0} ms/frame · decode {update.AverageDecodeMilliseconds:F0} ms/frame · recognition {update.AverageRecognitionMilliseconds:F0} ms/frame · {FormatPosition(update.Position)}";
            ProcessingDiagnostics = update.Diagnostics;
            processingItem.Progress = update.Fraction;
            processingItem.Detail = ProgressText;
        });
        try
        {
            _result = await _analysis.AnalyzeAsync(
                sourcePath,
                displayName,
                options,
                progress,
                message => MainThread.BeginInvokeOnMainThread(() => StatusMessage = message),
                _runCancellation.Token);
            await _repository.SaveAsync(_result, _runCancellation.Token);
            _savedResults = [_result, .. _savedResults.Where(result => result.Id != _result.Id)];
            _processingItem = null;
            RebuildAnalyses();
            Progress = 1;
            ProgressText = "100% · processing complete";
            StatusMessage = "Analysis saved. Select it below to review detections.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Processing cancelled. No analysis was saved.";
            ProgressText = "Cancelled";
            _processingItem = null;
            RebuildAnalyses();
        }
        catch (Exception exception)
        {
            StatusMessage = $"Video processing failed: {exception.Message}";
            ProgressText = "Processing failed";
            _processingItem = null;
            RebuildAnalyses();
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            IsProcessing = false;
            RefreshCommands();
        }
    }

    private async Task PreviousFrameAsync()
    {
        _currentFrameIndex--;
        await LoadCurrentFrameAsync(_result!.Frames[_currentFrameIndex].Position);
    }

    private async Task NextFrameAsync()
    {
        _currentFrameIndex++;
        await LoadCurrentFrameAsync(_result!.Frames[_currentFrameIndex].Position);
    }

    public async Task SeekToFractionAsync(double fraction)
    {
        if (_result is null || _result.Frames.Count == 0) return;
        var position = TimeSpan.FromTicks(checked((long)(_result.Duration.Ticks * Math.Clamp(fraction, 0, 1))));
        _currentFrameIndex = FindClosestFrameIndex(position);
        await LoadCurrentFrameAsync(_result.Frames[_currentFrameIndex].Position);
    }

    private async Task LoadCurrentFrameAsync(TimeSpan previewPosition)
    {
        if (_result is null || _currentFrameIndex < 0 || _currentFrameIndex >= _result.Frames.Count)
        {
            return;
        }

        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var frame = _result.Frames[_currentFrameIndex];
        CurrentFrame = frame;
        CurrentPositionFraction = _result.Duration.Ticks == 0 ? 0 : frame.Position.Ticks / (double)_result.Duration.Ticks;
        CurrentFrameTitle = $"{FormatPosition(frame.Position)} / {FormatPosition(_result.Duration)}";
        CurrentFrameDetail = $"Nearest analyzed frame {frame.SourceFrameIndex + 1:N0} · {frame.Reads.Count} reads · {frame.Confirmations.Count} confirmations";
        CurrentReads.Clear();
        foreach (var read in frame.Reads)
        {
            CurrentReads.Add($"{PlateText.FormatDutchPlate(PlateText.Normalize(read.Text))} · OCR {read.OcrConfidence:P0} · detector {read.DetectorConfidence:P0}");
        }
        foreach (var confirmation in frame.Confirmations)
        {
            CurrentReads.Add($"Confirmed {confirmation.DisplayPlate} · {confirmation.Confidence:P0}");
        }
        if (CurrentReads.Count == 0)
        {
            CurrentReads.Add("No plate observations near this point.");
        }
        else if (frame.SourceWidth <= 0 || frame.SourceHeight <= 0)
        {
            CurrentReads.Add("Bounding boxes were not saved with this older analysis. Process the video again to add them.");
        }

        RefreshCommands();
        if (!File.Exists(_result.SourcePath))
        {
            CurrentPreview = null;
            CurrentReads.Add("The source video is unavailable. Saved detections can still be reviewed.");
            return;
        }

        try
        {
            var cacheKey = previewPosition.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond;
            if (!_previewCache.TryGetValue(cacheKey, out var bytes))
            {
                bytes = await _analysis.GetPreviewAsync(_result.SourcePath, previewPosition, _previewCancellation.Token);
                AddPreviewToCache(cacheKey, bytes);
            }
            CurrentPreview = ImageSource.FromStream(() => new MemoryStream(bytes, writable: false));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CurrentPreview = null;
            CurrentReads.Add($"Preview unavailable: {exception.Message}");
        }
    }

    private void Cancel() => _runCancellation?.Cancel();

    private void ClearResult()
    {
        _result = null;
        Progress = 0;
        ProgressText = string.Empty;
        ProcessingDiagnostics = null;
        CurrentPreview = null;
        CurrentFrame = null;
        CurrentReads.Clear();
        DetectedPlates.Clear();
        DetectionMarkers = [];
        FramePositions = [];
        _previewCache.Clear();
        _previewCacheOrder.Clear();
        OnPropertyChanged(nameof(HasDetectedPlates));
        RefreshCommands();
    }

    private void PrepareReview()
    {
        if (_result is null) return;
        _previewCache.Clear();
        _previewCacheOrder.Clear();
        DetectionMarkers = _result.Duration.Ticks == 0
            ? []
            : _result.Frames.Where(static frame => frame.HasDetections)
                .Select(frame => frame.Position.Ticks / (double)_result.Duration.Ticks)
                .ToArray();
        FramePositions = _result.Duration.Ticks == 0
            ? [0]
            : _result.Frames.Select(frame => frame.Position.Ticks / (double)_result.Duration.Ticks).ToArray();
        DetectedPlates.Clear();
        foreach (var plateGroup in _result.Frames
                     .SelectMany(frame => frame.Confirmations.Select(confirmation => (Frame: frame, Confirmation: confirmation)))
                     .GroupBy(static item => item.Confirmation.NormalizedPlate, StringComparer.Ordinal))
        {
            var first = plateGroup.OrderBy(static item => item.Frame.Position).First();
            var strongest = plateGroup.MaxBy(static item => item.Confirmation.Confidence).Confirmation;
            var position = first.Frame.Position;
            DetectedPlates.Add(new AnalyzedPlateIndexItem(
                strongest.DisplayPlate,
                $"{FormatPosition(position)} · {strongest.Confidence:P0}",
                new Command(async () =>
                {
                    _currentFrameIndex = FindClosestFrameIndex(position);
                    await LoadCurrentFrameAsync(position);
                })));
        }
        OnPropertyChanged(nameof(HasDetectedPlates));
    }

    private void RebuildAnalyses()
    {
        Analyses.Clear();
        if (_processingItem is not null)
        {
            Analyses.Add(_processingItem);
        }
        foreach (var result in _savedResults)
        {
            var detectionCount = result.Frames.Sum(static frame => frame.Reads.Count);
            var uniquePlateCount = result.Frames.SelectMany(static frame => frame.Confirmations)
                .Select(static confirmation => confirmation.NormalizedPlate)
                .Distinct(StringComparer.Ordinal)
                .Count();
            Analyses.Add(new AnalysisListItem(
                result.DisplayName,
                $"{result.AnalyzedAt.LocalDateTime:g} · {FormatPosition(result.Duration)} · {detectionCount:N0} reads · {uniquePlateCount:N0} plates",
                File.Exists(result.SourcePath) ? "Video available" : "Analysis only · source video unavailable",
                false,
                new Command(async () => await OpenSavedAnalysisAsync(result)),
                new Command(async () => await DeleteAnalysisAsync(result))));
        }
        OnPropertyChanged(nameof(HasAnalyses));
    }

    private async Task DeleteAnalysisAsync(VideoAnalysisResult result)
    {
        try
        {
            await _repository.DeleteAsync(result.Id, CancellationToken.None);
            _savedResults = _savedResults.Where(item => item.Id != result.Id).ToArray();
            RebuildAnalyses();
            StatusMessage = $"Deleted analysis for {result.DisplayName}.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not delete the analysis: {exception.Message}";
        }
    }

    private async Task OpenSavedAnalysisAsync(VideoAnalysisResult result)
    {
        _result = result;
        PrepareReview();
        _currentFrameIndex = FirstDetectionFrameIndex();
        IsReviewing = true;
        OnPropertyChanged(nameof(ShowSetup));
        await LoadCurrentFrameAsync(result.Frames[_currentFrameIndex].Position);
        if (NeedsGeometryUpgrade(result) && File.Exists(result.SourcePath))
        {
            await UpgradeGeometryAsync(result);
        }
    }

    private async Task UpgradeGeometryAsync(VideoAnalysisResult legacyResult)
    {
        var selectedPosition = legacyResult.Frames[_currentFrameIndex].Position;
        IsProcessing = true;
        _runCancellation = new CancellationTokenSource();
        var progress = new Progress<VideoAnalysisProgress>(update =>
        {
            Progress = update.Fraction;
            ProcessingDiagnostics = update.Diagnostics;
            CurrentFrameDetail = $"Adding detection boxes · {update.Fraction:P0} · {update.ProcessedFrames:N0} of {update.TotalFrames:N0} frames";
        });
        try
        {
            var enriched = await _analysis.AnalyzeAsync(
                legacyResult.SourcePath,
                legacyResult.DisplayName,
                new VideoAnalysisOptions(
                    legacyResult.Sampling,
                    IncludeDiagnostics: RecognitionDebugEnabled),
                progress,
                message => MainThread.BeginInvokeOnMainThread(() => StatusMessage = message),
                _runCancellation.Token);
            enriched = enriched with
            {
                Id = legacyResult.Id,
                AnalyzedAt = legacyResult.AnalyzedAt
            };
            await _repository.SaveAsync(enriched, _runCancellation.Token);
            _savedResults = _savedResults.Select(item => item.Id == enriched.Id ? enriched : item).ToArray();
            _result = enriched;
            PrepareReview();
            _currentFrameIndex = FindClosestFrameIndex(selectedPosition);
            await LoadCurrentFrameAsync(enriched.Frames[_currentFrameIndex].Position);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CurrentReads.Add($"Could not add detection boxes: {exception.Message}");
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            IsProcessing = false;
            RebuildAnalyses();
        }
    }

    private static bool NeedsGeometryUpgrade(VideoAnalysisResult result) =>
        result.Frames.Any(static frame => frame.HasDetections && (frame.SourceWidth <= 0 || frame.SourceHeight <= 0));

    private int FirstDetectionFrameIndex() => Math.Max(0, _result!.Frames.ToList().FindIndex(static frame => frame.HasDetections));

    private int FindClosestFrameIndex(TimeSpan position)
    {
        var frames = _result?.Frames ?? [];
        return VideoFrameNavigation.FindClosestFrameIndex(frames, position);
    }

    private void AddPreviewToCache(long key, byte[] bytes)
    {
        _previewCache[key] = bytes;
        _previewCacheOrder.Enqueue(key);
        while (_previewCacheOrder.Count > MaximumPreviewCacheEntries)
        {
            _previewCache.Remove(_previewCacheOrder.Dequeue());
        }
    }

    private void RefreshCommands()
    {
        _processCommand.RaiseCanExecuteChanged();
        _previousFrameCommand.RaiseCanExecuteChanged();
        _nextFrameCommand.RaiseCanExecuteChanged();
        _cancelCommand.ChangeCanExecute();
    }

    private static string FormatPosition(TimeSpan position) => position.ToString(position.TotalHours >= 1 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff");
}
