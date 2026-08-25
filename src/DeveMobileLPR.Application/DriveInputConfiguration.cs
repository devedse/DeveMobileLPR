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
    bool IsLikelyCroppedMode = false,
    float? RelativeSensorArea = null);

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

public sealed record ConfiguredDriveSource(
    DriveSourceCapability Capability,
    DriveSourceProfile Profile);

public sealed record DriveInputConfigurationPlan(
    DriveInputConfiguration Configuration,
    IReadOnlyList<ConfiguredDriveSource> EnabledSources)
{
    public IReadOnlyList<ConfiguredDriveSource> IntegratedSources =>
        EnabledSources.Where(source => source.Capability.IsIntegratedCamera).ToArray();

    public ConfiguredDriveSource? NetworkSource => EnabledSources.FirstOrDefault(
        source => source.Capability.Kind == DriveSourceKind.NetworkLlHls);
}

/// <summary>
/// Applies platform-neutral input rules once so native adapters only translate the resulting plan
/// into CameraX, Camera2, MediaCapture, or network-source operations. Deliberately does not impose
/// a simultaneous-camera limit: platform/device APIs remain the authority for usable combinations.
/// </summary>
public static class DriveInputConfigurationPlanner
{
    public static DriveInputConfigurationPlan Create(
        DriveInputConfiguration configuration,
        IReadOnlyList<DriveSourceCapability> capabilities,
        bool supportsMultipleSources)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (configuration.Mode == DriveInputMode.Multi && !supportsMultipleSources)
        {
            throw new NotSupportedException("Multiple simultaneous sources are not supported on this platform.");
        }

        var capabilitiesById = capabilities
            .GroupBy(source => source.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var profiles = configuration.Sources
            .GroupBy(source => source.SourceId, StringComparer.Ordinal)
            .Select(group => NormalizeProfile(group.First(), capabilitiesById))
            .ToArray();
        var normalized = configuration with { Sources = profiles };
        var enabled = normalized.EnabledSources
            .Select(profile => capabilitiesById.TryGetValue(profile.SourceId, out var capability)
                ? new ConfiguredDriveSource(capability, profile)
                : null)
            .Where(static source => source is not null)
            .Select(static source => source!)
            .ToArray();

        if (enabled.Length == 0)
        {
            throw new InvalidOperationException("Enable at least one available video source.");
        }

        foreach (var source in enabled.Where(source => source.Capability.Kind == DriveSourceKind.NetworkLlHls))
        {
            if (!IsHttpUrl(source.Profile.NetworkUrl))
            {
                throw new InvalidOperationException("Enter a valid HTTP or HTTPS LL-HLS playlist URL.");
            }
        }

        return new DriveInputConfigurationPlan(normalized, enabled);
    }

    private static DriveSourceProfile NormalizeProfile(
        DriveSourceProfile profile,
        IReadOnlyDictionary<string, DriveSourceCapability> capabilities)
    {
        if (!capabilities.TryGetValue(profile.SourceId, out var capability))
        {
            return profile with { Enabled = false };
        }

        var resolution = capability.Kind == DriveSourceKind.NetworkLlHls
            ? profile.Resolution
            : SelectResolution(capability.Resolutions, profile.Resolution);
        return profile with
        {
            Resolution = resolution,
            Zoom = Math.Clamp(profile.Zoom, capability.MinimumZoom, capability.MaximumZoom),
            NetworkUrl = profile.NetworkUrl?.Trim()
        };
    }

    private static VideoResolution SelectResolution(
        IReadOnlyList<VideoResolution> available,
        VideoResolution requested)
    {
        if (available.Count == 0)
        {
            return requested;
        }

        return available.FirstOrDefault(size => size == requested)
            ?? available
                .OrderBy(size => size.Width >= requested.Width && size.Height >= requested.Height ? 0 : 1)
                .ThenBy(size => Math.Abs(size.PixelCount - requested.PixelCount))
                .First();
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

public sealed record SourceFrame(string SourceId, Yuv420Frame Frame);

public interface IDriveSourceCatalog
{
    bool SupportsMultipleSources { get; }
    int MaximumSimultaneousIntegratedSources { get; }
    Task<IReadOnlyList<DriveSourceCapability>> DiscoverAsync(CancellationToken cancellationToken = default);
}
