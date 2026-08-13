using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Application;

public interface IDriveSettings
{
    bool TrackLocation { get; set; }
    bool SaveVehicleImages { get; set; }
    bool ConfirmationHaptic { get; set; }
    KnownVehicleSound KnownVehicleSound { get; set; }
    float Zoom { get; set; }
    string CameraId { get; set; }
    int RecognitionFramesPerSecond { get; set; }
    bool TrackingDiagnosticsEnabled { get; set; }
    bool RecognitionStatisticsEnabled { get; set; }
    bool ShowRoadGuide { get; set; }
    string NetworkStreamUrl { get; set; }
    DriveInputConfiguration InputConfiguration { get; set; }
}

public enum KnownVehicleSound
{
    None,
    Chime,
    Radar,
    Sparkle
}

public interface IVehicleImageEncoder
{
    Task EncodeJpegAsync(
        ReadOnlyMemory<byte> rgbPixels,
        int width,
        int height,
        string destinationPath,
        CancellationToken cancellationToken);
}

public interface IVehicleImageStore
{
    Task<string> SaveAsync(
        long sightingId,
        Yuv420Frame frame,
        BoundingBox plateBounds,
        CancellationToken cancellationToken);

    string? ResolvePath(string? reference);
    Task DeleteAllAsync(CancellationToken cancellationToken);
}

public interface IRecognitionPipelineProvider
{
    Task<IFrameRecognitionPipeline> CreateAsync(
        Action<string>? diagnostic,
        CancellationToken cancellationToken);
}

public sealed record DriveInputDiagnostic(string Message, bool IsError = false);

public interface IDriveVideoInput : IDriveFrameSourceTelemetry, IAsyncDisposable
{
    event EventHandler<DriveInputDiagnostic>? Diagnostic;
    event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged;

    IReadOnlyList<CameraChoice> CameraChoices { get; }
    string SelectedCameraId { get; }
    bool IsReady { get; }
    bool SupportsNetworkStreams { get; }
    IReadOnlyList<DriveSourceCapability> SourceCapabilities { get; }

    Task InitializeAsync(string preferredCameraId, CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default);
    void SetZoom(float zoomRatio);
    void SetNetworkStreamUrl(string value);
    Task ApplyConfigurationAsync(
        DriveInputConfiguration configuration,
        CancellationToken cancellationToken = default);
}

public interface IDriveFrameSourceTelemetry
{
    event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
    event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented;
    bool ReportsPreviewFrames { get; }
}

public sealed class DriveFrameCountEventArgs(long count) : EventArgs
{
    public long Count { get; } = count > 0
        ? count
        : throw new ArgumentOutOfRangeException(nameof(count));
}

/// <summary>
/// A position together with the moment it was observed. The timestamp is what lets a consumer tell
/// a current fix from one the device happens to still be holding, which a bare coordinate cannot.
/// </summary>
public readonly record struct LocationFix(GeoPoint Point, DateTimeOffset ObservedAt);

public interface IDriveLocationTracker : IDisposable
{
    LocationFix? Latest { get; }
    Task<bool> StartAsync(CancellationToken cancellationToken);
    void Stop();
}

/// <summary>
/// Creates a tracker per drive. A tracker keeps the last fix it saw, so one shared for the lifetime
/// of the app would carry a position from a previous trip into the next one; a tracker that only
/// lives as long as its trip has nothing to carry.
/// </summary>
public interface IDriveLocationTrackerFactory
{
    IDriveLocationTracker Create();
}

public interface IDeviceExperience
{
    void SetKeepScreenOn(bool enabled);
    void NotifyPlateConfirmed();
    void NotifyKnownVehicle(KnownVehicleSound sound);
}

public interface IApplicationDispatcher
{
    void Dispatch(Action action);
}

public sealed record SelectedVideoFile(
    string FileName,
    string? FullPath,
    Func<CancellationToken, Task<Stream>> OpenReadAsync);

public interface IVideoFileBackend
{
    Task<string> StageAsync(SelectedVideoFile file, CancellationToken cancellationToken);
    Task<IVideoFrameSource> OpenFrameSourceAsync(string sourcePath, CancellationToken cancellationToken);
    Task<byte[]> GetPreviewAsync(string sourcePath, TimeSpan position, CancellationToken cancellationToken);
}

public interface IVehicleDataStatus
{
    bool IsAvailable { get; }
}
