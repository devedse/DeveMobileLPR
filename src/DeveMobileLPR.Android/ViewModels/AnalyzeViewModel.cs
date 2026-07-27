using System.Collections.ObjectModel;
using System.Windows.Input;
using DeveMobileLPR.AndroidApp.Services;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.AndroidApp.ViewModels;

internal sealed record FrameSamplingOption(string Name, string Detail, int Interval)
{
    public override string ToString() => Name;
}

internal sealed class AnalyzeViewModel : ViewModelBase
{
    private readonly VideoAnalysisService _analysis;
    private readonly AsyncCommand _processCommand;
    private readonly AsyncCommand _openReviewCommand;
    private readonly AsyncCommand _previousFrameCommand;
    private readonly AsyncCommand _nextFrameCommand;
    private readonly Command _cancelCommand;
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _previewCancellation;
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

    public AnalyzeViewModel(VideoAnalysisService analysis)
    {
        _analysis = analysis;
        SamplingOptions =
        [
            new("All frames", "Process every reported source frame", 1),
            new("Every 2nd frame", "Process half of the source frames", 2),
            new("Every 4th frame", "Process one quarter of the source frames", 4),
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
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ResultSummary { get => _resultSummary; private set => SetProperty(ref _resultSummary, value); }
    public ImageSource? CurrentPreview { get => _currentPreview; private set => SetProperty(ref _currentPreview, value); }
    public string CurrentFrameTitle { get => _currentFrameTitle; private set => SetProperty(ref _currentFrameTitle, value); }
    public string CurrentFrameDetail { get => _currentFrameDetail; private set => SetProperty(ref _currentFrameDetail, value); }

    public FrameSamplingOption SelectedSampling
    {
        get => _selectedSampling;
        set => SetProperty(ref _selectedSampling, value);
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
            StatusMessage = "Ready to process. The video stays in the app cache on this device.";
            OnPropertyChanged(nameof(HasSelectedFile));
            _processCommand.RaiseCanExecuteChanged();
            if (previousPath is not null)
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
        Progress = 0;
        ProgressText = "Preparing models…";
        StatusMessage = "Processing sampled frames on this device.";
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
            Progress = 1;
            ProgressText = "100% · processing complete";
            var observationCount = _result.Frames.Sum(static frame => frame.Recognition.Observations.Count);
            var confirmationCount = _result.Frames.Sum(static frame => frame.Confirmations.Count);
            ResultSummary = $"{_result.Frames.Count:N0} frames reviewed · {observationCount:N0} plate reads · {confirmationCount:N0} confirmations";
            StatusMessage = "Analysis complete. Open the review to inspect each processed frame.";
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(HasReviewFrames));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Processing cancelled. No sightings were added to History.";
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

        _currentFrameIndex = 0;
        IsReviewing = true;
        OnPropertyChanged(nameof(ShowSetup));
        await LoadCurrentFrameAsync();
    }

    private async Task PreviousFrameAsync()
    {
        _currentFrameIndex--;
        await LoadCurrentFrameAsync();
    }

    private async Task NextFrameAsync()
    {
        _currentFrameIndex++;
        await LoadCurrentFrameAsync();
    }

    private async Task LoadCurrentFrameAsync()
    {
        if (_result is null || _currentFrameIndex < 0 || _currentFrameIndex >= _result.Frames.Count)
        {
            return;
        }

        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var frame = _result.Frames[_currentFrameIndex];
        CurrentFrameTitle = $"Frame {frame.SourceFrameIndex + 1:N0} · {FormatPosition(frame.Position)}";
        CurrentFrameDetail = $"Processed {_currentFrameIndex + 1:N0} of {_result.Frames.Count:N0} · {frame.Recognition.Observations.Count} reads · {frame.Confirmations.Count} confirmations";
        CurrentReads.Clear();
        foreach (var observation in frame.Recognition.Observations)
        {
            CurrentReads.Add($"{PlateText.FormatDutchPlate(PlateText.Normalize(observation.Read.Text))} · OCR {observation.Read.Confidence:P0} · detector {observation.Detection.Confidence:P0}");
        }
        foreach (var confirmation in frame.Confirmations)
        {
            CurrentReads.Add($"Confirmed {confirmation.Consensus.DisplayPlate} · {confirmation.Consensus.Confidence:P0}");
        }
        if (CurrentReads.Count == 0)
        {
            CurrentReads.Add("No plate observations in this processed frame.");
        }

        RefreshCommands();
        try
        {
            var bytes = await _analysis.GetPreviewAsync(_result.SourcePath, frame.Position, _previewCancellation.Token);
            CurrentPreview = ImageSource.FromStream(() => new MemoryStream(bytes, writable: false));
        }
        catch (OperationCanceledException)
        {
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
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasReviewFrames));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        _processCommand.RaiseCanExecuteChanged();
        _openReviewCommand.RaiseCanExecuteChanged();
        _previousFrameCommand.RaiseCanExecuteChanged();
        _nextFrameCommand.RaiseCanExecuteChanged();
        _cancelCommand.ChangeCanExecute();
    }

    private static string FormatPosition(TimeSpan position) => position.ToString(position.TotalHours >= 1 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff");
}