using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Runtime;
using Android.Util;
using DeveMobileLPR.Application;
using AndroidSize = Android.Util.Size;
using AndroidSizeF = Android.Util.SizeF;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class AndroidDriveSourceCatalog(Context context) : IDriveSourceCatalog
{
    private readonly CameraManager _manager = context.GetSystemService(Context.CameraService) as CameraManager
        ?? throw new InvalidOperationException("Android returned no CameraManager.");

    public Task<IReadOnlyList<DriveSourceCapability>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var publicSources = new List<DriveSourceCapability>();
        var physicalSources = new List<PhysicalMetadata>();

        foreach (var cameraId in _manager.GetCameraIdList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var characteristics = _manager.GetCameraCharacteristics(cameraId);
            var facing = ReadInteger(characteristics, CameraCharacteristics.LensFacing);
            var isBack = facing == (int)LensFacing.Back;
            var isFront = facing == (int)LensFacing.Front;
            if (!isBack && !isFront)
            {
                continue;
            }

            var resolutions = ReadYuvResolutions(characteristics);
            var zoomMaximum = ReadFloat(characteristics, CameraCharacteristics.ScalerAvailableMaxDigitalZoom) ?? 1f;
            publicSources.Add(new DriveSourceCapability(
                isBack ? "rear" : "front",
                isBack ? "Rear cameras · automatic lens" : "Front camera",
                DriveSourceKind.LogicalCamera,
                true,
                cameraId,
                null,
                ReadFirstFloat(characteristics, CameraCharacteristics.LensInfoAvailableFocalLengths),
                ReadSizeF(characteristics)?.Width,
                ReadSizeF(characteristics)?.Height,
                1f,
                Math.Max(1f, zoomMaximum),
                resolutions,
                isFront ? InferredLensRole.Front : InferredLensRole.Unknown));

            var physicalIds = OperatingSystem.IsAndroidVersionAtLeast(28)
                ? characteristics.PhysicalCameraIds
                : null;
            foreach (var physicalId in physicalIds ?? [])
            {
                var physical = _manager.GetCameraCharacteristics(physicalId);
                var sensor = ReadSizeF(physical);
                physicalSources.Add(new PhysicalMetadata(
                    cameraId,
                    physicalId,
                    ReadFirstFloat(physical, CameraCharacteristics.LensInfoAvailableFocalLengths),
                    sensor?.Width,
                    sensor?.Height,
                    Math.Max(1f, ReadFloat(physical, CameraCharacteristics.ScalerAvailableMaxDigitalZoom) ?? 1f),
                    ReadYuvResolutions(physical),
                    isFront));
            }
        }

        var inferred = InferPhysicalSources(physicalSources);
        IReadOnlyList<DriveSourceCapability> result =
        [
            .. publicSources
                .GroupBy(source => source.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(source => source.Id == "rear" ? 0 : 1),
            .. inferred,
            new(
                DriveInputIds.NetworkLlHls,
                "OME LL-HLS stream",
                DriveSourceKind.NetworkLlHls,
                false,
                null,
                null,
                null,
                null,
                null,
                1f,
                1f,
                [])
        ];
        return Task.FromResult(result);
    }

    private static IReadOnlyList<DriveSourceCapability> InferPhysicalSources(
        IReadOnlyList<PhysicalMetadata> physicalSources)
    {
        var result = new List<DriveSourceCapability>();
        foreach (var facingGroup in physicalSources.GroupBy(source => source.IsFront))
        {
            var focalGroups = facingGroup
                .GroupBy(source => Math.Round(source.FocalLength ?? 0f, 2))
                .OrderBy(group => group.Key)
                .ToArray();

            for (var focalIndex = 0; focalIndex < focalGroups.Length; focalIndex++)
            {
                var focalGroup = focalGroups[focalIndex].ToArray();
                var role = facingGroup.Key
                    ? InferredLensRole.Front
                    : focalGroups.Length == 1
                        ? InferredLensRole.Main
                        : focalIndex == 0
                            ? InferredLensRole.Ultrawide
                            : focalIndex == focalGroups.Length - 1
                                ? InferredLensRole.Telephoto
                                : InferredLensRole.Main;
                var largestSensorArea = focalGroup.Max(source =>
                    (source.SensorWidth ?? 0f) * (source.SensorHeight ?? 0f));

                foreach (var source in focalGroup.OrderByDescending(item =>
                    (item.SensorWidth ?? 0f) * (item.SensorHeight ?? 0f)))
                {
                    var sensorArea = (source.SensorWidth ?? 0f) * (source.SensorHeight ?? 0f);
                    var cropped = largestSensorArea > 0f && sensorArea < largestSensorArea * 0.8f;
                    var relativeArea = largestSensorArea > 0f ? sensorArea / largestSensorArea : 1f;
                    var isPrimary = ReferenceEquals(source, focalGroup.MaxBy(item =>
                        (item.SensorWidth ?? 0f) * (item.SensorHeight ?? 0f)));
                    var roleName = role switch
                    {
                        InferredLensRole.Main => "main",
                        InferredLensRole.Ultrawide => "ultrawide",
                        InferredLensRole.Telephoto => "telephoto",
                        InferredLensRole.Front => "front",
                        _ => "camera"
                    };
                    var mode = isPrimary
                        ? "primary"
                        : cropped ? "cropped alternate" : "alternate";
                    result.Add(new DriveSourceCapability(
                        $"physical:{source.LogicalCameraId}:{source.PhysicalCameraId}",
                        $"ID {source.PhysicalCameraId} · {roleName} {mode} · {source.FocalLength:0.##} mm",
                        DriveSourceKind.PhysicalCamera,
                        true,
                        source.LogicalCameraId,
                        source.PhysicalCameraId,
                        source.FocalLength,
                        source.SensorWidth,
                        source.SensorHeight,
                        1f,
                        source.MaximumZoom,
                        source.Resolutions,
                        role,
                        cropped,
                        relativeArea));
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<VideoResolution> ReadYuvResolutions(CameraCharacteristics characteristics)
    {
        var map = characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap)
            as StreamConfigurationMap;
        return map?.GetOutputSizes((int)ImageFormatType.Yuv420888)?
            .Select(size => new VideoResolution(size.Width, size.Height))
            .Distinct()
            .OrderByDescending(size => size.PixelCount)
            .ToArray() ?? [];
    }

    private static int? ReadInteger(CameraCharacteristics characteristics, CameraCharacteristics.Key? key) =>
        key is null ? null : (characteristics.Get(key) as Java.Lang.Integer)?.IntValue();

    private static float? ReadFloat(CameraCharacteristics characteristics, CameraCharacteristics.Key? key) =>
        key is null ? null : (characteristics.Get(key) as Java.Lang.Float)?.FloatValue();

    private static float? ReadFirstFloat(CameraCharacteristics characteristics, CameraCharacteristics.Key? key)
    {
        if (key is null)
        {
            return null;
        }

        using var value = characteristics.Get(key);
        if (value is null)
        {
            return null;
        }

#pragma warning disable CS0618
        var values = JNIEnv.GetArray<float>(value.Handle);
#pragma warning restore CS0618
        return values is { Length: > 0 } ? values[0] : null;
    }

    private static AndroidSizeF? ReadSizeF(CameraCharacteristics characteristics) =>
        characteristics.Get(CameraCharacteristics.SensorInfoPhysicalSize) as AndroidSizeF;

    private sealed record PhysicalMetadata(
        string LogicalCameraId,
        string PhysicalCameraId,
        float? FocalLength,
        float? SensorWidth,
        float? SensorHeight,
        float MaximumZoom,
        IReadOnlyList<VideoResolution> Resolutions,
        bool IsFront);
}
