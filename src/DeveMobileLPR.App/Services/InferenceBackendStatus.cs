using System.ComponentModel;

namespace DeveMobileLPR.App.Services;

public sealed class InferenceBackendStatus : INotifyPropertyChanged
{
    private string _summary = "AI backend · waiting for initialization";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Summary
    {
        get => _summary;
        private set
        {
            if (_summary == value)
            {
                return;
            }

            _summary = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }
    }

    public void ReportInitializing(string message) => SetSummary($"AI backend · {message}");

    public void ReportSelected(string detectorBackend, string ocrBackend, string? state = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detectorBackend);
        ArgumentException.ThrowIfNullOrWhiteSpace(ocrBackend);
        var suffix = string.IsNullOrWhiteSpace(state) ? string.Empty : $" · {state}";
        SetSummary($"Detector: {detectorBackend} · OCR: {ocrBackend}{suffix}");
    }

    public void ReportFailure(string message) => SetSummary($"AI backend failed · {message}");

    private void SetSummary(string summary)
    {
        if (MainThread.IsMainThread)
        {
            Summary = summary;
            return;
        }

        MainThread.BeginInvokeOnMainThread(() => Summary = summary);
    }
}
