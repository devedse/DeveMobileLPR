using DeveMobileLPR.Imaging;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application;

public enum DriveInputMode
{
    Single,
    Multi
}

public enum DriveSourceKind
{
    LogicalCamera,
    PhysicalCamera,
    NetworkLlHls
}

public enum InferredLensRole
{
    Unknown,
    Main,
    Ultrawide,
    Telephoto,
    Front
}

public sealed record VideoResolution(int Width, int Height)
{
    public long PixelCount => (long)Width * Height;
    public override string ToString() => $"{Width}×{Height}";
}

public sealed record DriveSourceCapability(
    string Id,
    string Name,
    DriveSourceKind Kind,
    bool IsIntegratedCamera,
    string? LogicalCameraId,
    string? PhysicalCameraId,
    float? FocalLengthMillimeters,
    float? SensorWidthMillimeters,
    float? SensorHeightMillimeters,
    float MinimumZoom,
    float MaximumZoom,
    IReadOnlyList<VideoResolution> Resolutions,
    InferredLensRole InferredRole = InferredLensRole.Unknown,
    bool IsLikelyCroppedMode = false);

public sealed record DriveSourceProfile(
    string SourceId,
    bool Enabled,
    VideoResolution Resolution,
    float Zoom,
    string? NetworkUrl = null);

public sealed record DriveInputConfiguration(
    int Version,
    DriveInputMode Mode,
    IReadOnlyList<DriveSourceProfile> Sources,
    string? SelectedSingleSourceId = null)
{
    public const int CurrentVersion = 1;

    public static DriveInputConfiguration Default { get; } = new(
        CurrentVersion,
        DriveInputMode.Single,
        [new("rear", true, new VideoResolution(3840, 2160), 1f)],
        "rear");

    public IReadOnlyList<DriveSourceProfile> EnabledSources => Mode == DriveInputMode.Single
        ? Sources.Where(source => source.SourceId == (SelectedSingleSourceId ?? "rear")).Take(1).ToArray()
        : Sources.Where(source => source.Enabled).ToArray();
}

public sealed record SourceFrame(string SourceId, Yuv420Frame Frame);

public interface IDriveSourceCatalog
{
    Task<IReadOnlyList<DriveSourceCapability>> DiscoverAsync(CancellationToken cancellationToken = default);
}
