using DeveMobileLPR.Application;

namespace DeveMobileLPR.App.Controls;

/// <summary>Shared XAML composition of the native preview surface and MAUI detection overlay.</summary>
public partial class DrivePreviewPresenter : ContentView
{
    public static readonly BindableProperty IsMultiSourceProperty = BindableProperty.Create(
        nameof(IsMultiSource), typeof(bool), typeof(DrivePreviewPresenter), false);

    public static readonly BindableProperty IsNetworkStreamProperty = BindableProperty.Create(
        nameof(IsNetworkStream), typeof(bool), typeof(DrivePreviewPresenter), false);

    public static readonly BindableProperty OverlaysProperty = BindableProperty.Create(
        nameof(Overlays), typeof(IReadOnlyList<DriveOverlay>), typeof(DrivePreviewPresenter));

    public static readonly BindableProperty SourceIdsProperty = BindableProperty.Create(
        nameof(SourceIds), typeof(IReadOnlyList<string>), typeof(DrivePreviewPresenter));

    public static readonly BindableProperty ShowGuideProperty = BindableProperty.Create(
        nameof(ShowGuide), typeof(bool), typeof(DrivePreviewPresenter), false);

    public DrivePreviewPresenter() => InitializeComponent();

    public bool IsMultiSource
    {
        get => (bool)GetValue(IsMultiSourceProperty);
        set => SetValue(IsMultiSourceProperty, value);
    }

    public bool IsNetworkStream
    {
        get => (bool)GetValue(IsNetworkStreamProperty);
        set => SetValue(IsNetworkStreamProperty, value);
    }

    public IReadOnlyList<DriveOverlay>? Overlays
    {
        get => (IReadOnlyList<DriveOverlay>?)GetValue(OverlaysProperty);
        set => SetValue(OverlaysProperty, value);
    }

    public IReadOnlyList<string>? SourceIds
    {
        get => (IReadOnlyList<string>?)GetValue(SourceIdsProperty);
        set => SetValue(SourceIdsProperty, value);
    }

    public bool ShowGuide
    {
        get => (bool)GetValue(ShowGuideProperty);
        set => SetValue(ShowGuideProperty, value);
    }

    internal async Task<long> WaitForInputGenerationAsync(TimeSpan timeout)
    {
        if (Preview.InputGeneration > 0)
        {
            return Preview.InputGeneration;
        }

        var completion = new TaskCompletionSource<long>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(CameraPreview.InputGeneration)
                && Preview.InputGeneration > 0)
            {
                completion.TrySetResult(Preview.InputGeneration);
            }
        }

        Preview.PropertyChanged += Observe;
        try
        {
            return Preview.InputGeneration > 0
                ? Preview.InputGeneration
                : await completion.Task.WaitAsync(timeout);
        }
        finally
        {
            Preview.PropertyChanged -= Observe;
        }
    }
}
