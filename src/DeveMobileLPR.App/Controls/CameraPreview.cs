using DeveMobileLPR.Geometry;

namespace DeveMobileLPR.App.Controls;

/// <summary>
/// Hosts the platform's live preview surface. The handler reports how that surface fits the source
/// frame, because an overlay drawn on top has to project detections the same way or the boxes drift
/// away from the plates.
/// </summary>
internal sealed class CameraPreview : View
{
    /// <summary>Whether the active input is a network stream rather than a local camera.</summary>
    public static readonly BindableProperty IsNetworkStreamProperty = BindableProperty.Create(
        nameof(IsNetworkStream),
        typeof(bool),
        typeof(CameraPreview),
        false);

    public static readonly BindableProperty IsMultiSourceProperty = BindableProperty.Create(
        nameof(IsMultiSource),
        typeof(bool),
        typeof(CameraPreview),
        false);

    private static readonly BindablePropertyKey ScaleModePropertyKey = BindableProperty.CreateReadOnly(
        nameof(ScaleMode),
        typeof(AspectScaleMode),
        typeof(CameraPreview),
        AspectScaleMode.Fit);

    public static readonly BindableProperty ScaleModeProperty = ScaleModePropertyKey.BindableProperty;

    private static readonly BindablePropertyKey SourceViewportsPropertyKey = BindableProperty.CreateReadOnly(
        nameof(SourceViewports),
        typeof(IReadOnlyList<PreviewSourceViewport>),
        typeof(CameraPreview),
        Array.Empty<PreviewSourceViewport>());

    public static readonly BindableProperty SourceViewportsProperty = SourceViewportsPropertyKey.BindableProperty;

    private static readonly BindablePropertyKey InputGenerationPropertyKey = BindableProperty.CreateReadOnly(
        nameof(InputGeneration),
        typeof(long),
        typeof(CameraPreview),
        0L);

    public static readonly BindableProperty InputGenerationProperty = InputGenerationPropertyKey.BindableProperty;

    public CameraPreview()
    {
        AutomationId = "drive_camera_preview";
        SemanticProperties.SetDescription(this, "Live camera preview with on-device license plate detections");
    }

    public bool IsNetworkStream
    {
        get => (bool)GetValue(IsNetworkStreamProperty);
        set => SetValue(IsNetworkStreamProperty, value);
    }

    public bool IsMultiSource
    {
        get => (bool)GetValue(IsMultiSourceProperty);
        set => SetValue(IsMultiSourceProperty, value);
    }

    /// <summary>Actual native source panels, normalized to this preview host.</summary>
    public IReadOnlyList<PreviewSourceViewport> SourceViewports
    {
        get => (IReadOnlyList<PreviewSourceViewport>)GetValue(SourceViewportsProperty);
    }

    /// <summary>The fit actually applied by the platform preview; read-only to XAML consumers.</summary>
    public AspectScaleMode ScaleMode => (AspectScaleMode)GetValue(ScaleModeProperty);

    /// <summary>
    /// Identity of the native input owned by this exact preview handler. A drive may only start
    /// against this generation, which prevents a newly opened page from using a retiring camera.
    /// </summary>
    public long InputGeneration => (long)GetValue(InputGenerationProperty);

    internal void ReportPresentation(
        AspectScaleMode scaleMode,
        IReadOnlyList<PreviewSourceViewport>? sourceViewports = null)
    {
        SetValue(ScaleModePropertyKey, scaleMode);
        if (sourceViewports is not null)
        {
            SetValue(SourceViewportsPropertyKey, sourceViewports);
        }
    }

    internal void ReportSourceViewports(IReadOnlyList<PreviewSourceViewport> sourceViewports) =>
        SetValue(SourceViewportsPropertyKey, sourceViewports);

    internal void ReportInputGeneration(long generation) =>
        SetValue(InputGenerationPropertyKey, generation);
}
