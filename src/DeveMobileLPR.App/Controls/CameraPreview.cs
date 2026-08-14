using DeveMobileLPR.Geometry;

namespace DeveMobileLPR.App.Controls;

/// <summary>
/// Hosts the platform's live preview surface. The handler reports how that surface fits the source
/// frame, because an overlay drawn on top has to project detections the same way or the boxes drift
/// away from the plates.
/// </summary>
internal sealed class CameraPreview : View
{
    /// <summary>How the platform preview control fits camera frames. Set by the handler.</summary>
    public static readonly BindableProperty CameraScaleModeProperty = BindableProperty.Create(
        nameof(CameraScaleMode),
        typeof(AspectScaleMode),
        typeof(CameraPreview),
        AspectScaleMode.Fit,
        propertyChanged: static (bindable, _, _) => ((CameraPreview)bindable).OnPropertyChanged(nameof(ScaleMode)));

    /// <summary>How the platform preview control fits network stream frames. Set by the handler.</summary>
    public static readonly BindableProperty StreamScaleModeProperty = BindableProperty.Create(
        nameof(StreamScaleMode),
        typeof(AspectScaleMode),
        typeof(CameraPreview),
        AspectScaleMode.Fit,
        propertyChanged: static (bindable, _, _) => ((CameraPreview)bindable).OnPropertyChanged(nameof(ScaleMode)));

    /// <summary>Whether the active input is a network stream rather than a local camera.</summary>
    public static readonly BindableProperty IsNetworkStreamProperty = BindableProperty.Create(
        nameof(IsNetworkStream),
        typeof(bool),
        typeof(CameraPreview),
        false,
        propertyChanged: static (bindable, _, _) => ((CameraPreview)bindable).OnPropertyChanged(nameof(ScaleMode)));

    public static readonly BindableProperty IsMultiSourceProperty = BindableProperty.Create(
        nameof(IsMultiSource),
        typeof(bool),
        typeof(CameraPreview),
        false,
        propertyChanged: static (bindable, _, _) => ((CameraPreview)bindable).OnPropertyChanged(nameof(ScaleMode)));

    public static readonly BindableProperty SourceViewportsProperty = BindableProperty.Create(
        nameof(SourceViewports),
        typeof(IReadOnlyList<PreviewSourceViewport>),
        typeof(CameraPreview),
        Array.Empty<PreviewSourceViewport>());

    public CameraPreview()
    {
        AutomationId = "drive_camera_preview";
        SemanticProperties.SetDescription(this, "Live camera preview with on-device license plate detections");
    }

    public AspectScaleMode CameraScaleMode
    {
        get => (AspectScaleMode)GetValue(CameraScaleModeProperty);
        set => SetValue(CameraScaleModeProperty, value);
    }

    public AspectScaleMode StreamScaleMode
    {
        get => (AspectScaleMode)GetValue(StreamScaleModeProperty);
        set => SetValue(StreamScaleModeProperty, value);
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
        set => SetValue(SourceViewportsProperty, value);
    }

    /// <summary>The fit currently applied to the visible surface; bind an overlay's scale mode to this.</summary>
    public AspectScaleMode ScaleMode => IsNetworkStream || IsMultiSource
        ? StreamScaleMode
        : CameraScaleMode;
}
