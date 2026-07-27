using System.Collections.ObjectModel;
using System.Windows.Input;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.App.ViewModels;

internal sealed record FrameSamplingOption(string Name, string Detail, int Interval)
{
    public override string ToString() => Name;
}

internal sealed record SavedAnalysisListItem(string Title, string Detail, string SourceStatus, ICommand OpenCommand);
internal sealed record AnalyzedPlateIndexItem(string DisplayPlate, string Detail, ICommand OpenCommand);

internal sealed class AnalyzeViewModel : ViewModelBase
{
    private const int MaximumPreviewCacheEntries = 8;
    private readonly VideoAnalysisService _analysis;
    private readonly JsonVideoAnalysisRepository _repository;
    private readonly AsyncCommand _processCommand;
    private readonly AsyncCommand _openReviewCommand;
    private readonly AsyncCommand _previousFrameCommand;
    private readonly AsyncCommand _nextFrameCommand;
    private readonly Command _cancelCommand;
    private readonly Dictionary<long, byte[]> _previewCache = [];
    private readonly Queue<long> _previewCacheOrder = [];
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _previewCancellation;
    private IReadOnlyList<VideoAnalysisResult> _savedResults = [];
    private VideoAnalysisResult? _result;
    private string? _stagedPath;
    private string _selectedFileName = "No video selected";
    private FrameSamplingOption _selectedSampling;
    private bool _isProcessing;
    private bool _isReviewing;
    private double _progress;
    private string _progressText = string.Empty;
    private string _statusMessage = "Select a video to create a private, on-device analysis run.";
    private string _resultSummary = string.Empty;
    private int _currentFrameIndex;
    private ImageSource? _currentPreview;
    private string _currentFrameTitle = string.Empty;
    private string _currentFrameDetail = string.Empty;
    private double _currentPositionFraction;
    private IReadOnlyList<double> _detectionMarkers = [];
    private bool _initialized;

    public AnalyzeViewModel(VideoAnalysisService analysis, JsonVideoAnalysisRepository repository)
    {
        _analysis = analysis;
        _repository = repository;
        SamplingOptions =
        [
            new("All frames", "Process every reported source frame", 1),
            new("Every 2nd frame", "Process half of the source frames", 2),
            new("Every 4th frame", "Balanced analysis for most recordings", 4),
            new("Every 8th frame", "Faster exploratory pass", 8)
        ];
        _selectedSampling = SamplingOptions[2];
        _processCommand = new AsyncCommand(ProcessAsync, () => HasSelectedFile && !IsProcessing);
        _openReviewCommand = new AsyncCommand(OpenReviewAsync, () => HasReviewFrames && !IsProcessing);
        _previousFrameCommand = new AsyncCommand(PreviousFrameAsync, () => IsReviewing && _currentFrameIndex > 0);
        _nextFrameCommand = new AsyncCommand(NextFrameAsync, () => IsReviewing && _result is not null && _currentFrameIndex < _result.Frames.Count - 1);
        _cancelCommand = new Command(Cancel, () => IsProcessing);
    }

    public IReadOnlyList<FrameSamplingOption> SamplingOptions { get; }
    public ObservableCollection<string> CurrentReads { get; } = [];
    public ObservableCollection<SavedAnalysisListItem> SavedAnalyses { get; } = [];
    public ObservableCollection<AnalyzedPlateIndexItem> DetectedPlates { get; } = [];
    public ICommand ProcessCommand => _processCommand;
    public ICommand OpenReviewCommand => _openReviewCommand;
    public ICommand PreviousFrameCommand => _previousFrameCommand;
    public ICommand NextFrameCommand => _nextFrameCommand;
    public ICommand CancelCommand => _cancelCommand;
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
                RefreshCommands();
            }
        }
    }
    public bool CanSelectVideo => !IsProcessing;
    public bool IsReviewing { get => _isReviewing; private set { if (SetProperty(ref _isReviewing, value)) RefreshCommands(); } }
    public bool ShowSetup => !IsReviewing;
    public bool HasResult => _result is not null;
    public bool HasReviewFrames => _result?.Frames.Count > 0;
    public bool HasSavedAnalyses => SavedAnalyses.Count > 0;
    public bool HasDetectedPlates => DetectedPlates.Count > 0;
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ResultSummary { get => _resultSummary; private set => SetProperty(ref _resultSummary, value); }
    public ImageSource? CurrentPreview { get => _currentPreview; private set => SetProperty(ref _currentPreview, value); }
    public string CurrentFrameTitle { get => _currentFrameTitle; private set => SetProperty(ref _currentFrameTitle, value); }
    public string CurrentFrameDetail { get => _currentFrameDetail; private set => SetProperty(ref _currentFrameDetail, value); }
    public double CurrentPositionFraction { get => _currentPositionFraction; private set => SetProperty(ref _currentPositionFraction, value); }
    public IReadOnlyList<double> DetectionMarkers { get => _detectionMarkers; private set => SetProperty(ref _detectionMarkers, value); }

    public FrameSamplingOption SelectedSampling
    {
        get => _selectedSampling;
        set => SetProperty(ref _selectedSampling, value);
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        _savedResults = await _repository.LoadAllAsync(CancellationToken.None);
        RebuildSavedAnalyses();
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
            _stagedPath = await _analysis.StageAsync(file, CancellationToken.None);
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
        _previewCancellation?.Cancel();
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

        ClearResult();
        IsProcessing = true;
        ProgressText = "Preparing models…";
        StatusMessage = "Processing scaled frames on this device.";
        _runCancellation = new CancellationTokenSource();
        var progress = new Progress<VideoAnalysisProgress>(update =>
        {
            Progress = update.Fraction;
            ProgressText = $"{update.Fraction:P0} · {update.ProcessedFrames:N0} of {update.TotalFrames:N0} frames · {FormatPosition(update.Position)}";
        });
        try
        {
            _result = await _analysis.AnalyzeAsync(
                _stagedPath,
                SelectedFileName,
                new VideoFrameSampling(SelectedSampling.Interval),
                progress,
                _runCancellation.Token);
            await _repository.SaveAsync(_result, _runCancellation.Token);
            _savedResults = [_result, .. _savedResults.Where(result => result.Id != _result.Id)];
            RebuildSavedAnalyses();
            Progress = 1;
            ProgressText = "100% · processing complete";
            ResultSummary = BuildResultSummary(_result);
            StatusMessage = "Analysis saved. Open the timeline to review detections.";
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(HasReviewFrames));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Processing cancelled. No analysis was saved.";
            ProgressText = "Cancelled";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Video processing failed: {exception.Message}";
            ProgressText = "Processing failed";
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            IsProcessing = false;
            RefreshCommands();
        }
    }

    private async Task OpenReviewAsync()
    {
        if (!HasReviewFrames)
        {
            return;
        }

        PrepareReview();
        _currentFrameIndex = FirstDetectionFrameIndex();
        IsReviewing = true;
        OnPropertyChanged(nameof(ShowSetup));
        await LoadCurrentFrameAsync(_result!.Frames[_currentFrameIndex].Position);
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
        await LoadCurrentFrameAsync(position);
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
        CurrentPositionFraction = _result.Duration.Ticks == 0 ? 0 : previewPosition.Ticks / (double)_result.Duration.Ticks;
        CurrentFrameTitle = $"{FormatPosition(previewPosition)} / {FormatPosition(_result.Duration)}";
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
        ResultSummary = string.Empty;
        Progress = 0;
        ProgressText = string.Empty;
        CurrentPreview = null;
        CurrentReads.Clear();
        DetectedPlates.Clear();
        DetectionMarkers = [];
        _previewCache.Clear();
        _previewCacheOrder.Clear();
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasReviewFrames));
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

    private void RebuildSavedAnalyses()
    {
        SavedAnalyses.Clear();
        foreach (var result in _savedResults)
        {
            var detectionCount = result.Frames.Sum(static frame => frame.Reads.Count);
            var uniquePlateCount = result.Frames.SelectMany(static frame => frame.Confirmations)
                .Select(static confirmation => confirmation.NormalizedPlate)
                .Distinct(StringComparer.Ordinal)
                .Count();
            SavedAnalyses.Add(new SavedAnalysisListItem(
                result.DisplayName,
                $"{result.AnalyzedAt.LocalDateTime:g} · {FormatPosition(result.Duration)} · {detectionCount:N0} reads · {uniquePlateCount:N0} plates",
                File.Exists(result.SourcePath) ? "Video available" : "Analysis only · source video unavailable",
                new Command(async () => await OpenSavedAnalysisAsync(result))));
        }
        OnPropertyChanged(nameof(HasSavedAnalyses));
    }

    private async Task OpenSavedAnalysisAsync(VideoAnalysisResult result)
    {
        _result = result;
        _stagedPath = result.SourcePath;
        SelectedFileName = result.DisplayName;
        ResultSummary = BuildResultSummary(result);
        OnPropertyChanged(nameof(HasSelectedFile));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasReviewFrames));
        PrepareReview();
        _currentFrameIndex = FirstDetectionFrameIndex();
        IsReviewing = true;
        OnPropertyChanged(nameof(ShowSetup));
        await LoadCurrentFrameAsync(result.Frames[_currentFrameIndex].Position);
    }

    private int FirstDetectionFrameIndex() => Math.Max(0, _result!.Frames.ToList().FindIndex(static frame => frame.HasDetections));

    private int FindClosestFrameIndex(TimeSpan position)
    {
        var frames = _result?.Frames ?? [];
        var low = 0;
        var high = frames.Count - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (frames[middle].Position < position) low = middle + 1;
            else high = middle;
        }
        return low > 0 && position - frames[low - 1].Position <= frames[low].Position - position ? low - 1 : low;
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
        _openReviewCommand.RaiseCanExecuteChanged();
        _previousFrameCommand.RaiseCanExecuteChanged();
        _nextFrameCommand.RaiseCanExecuteChanged();
        _cancelCommand.ChangeCanExecute();
    }

    private static string BuildResultSummary(VideoAnalysisResult result)
    {
        var observationCount = result.Frames.Sum(static frame => frame.Reads.Count);
        var confirmationCount = result.Frames.Sum(static frame => frame.Confirmations.Count);
        return $"{result.Frames.Count:N0} frames reviewed · {observationCount:N0} plate reads · {confirmationCount:N0} confirmations";
    }

    private static string FormatPosition(TimeSpan position) => position.ToString(position.TotalHours >= 1 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff");
}