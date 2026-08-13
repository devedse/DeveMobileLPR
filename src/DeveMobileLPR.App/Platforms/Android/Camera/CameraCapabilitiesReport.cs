using System.Globalization;
using Android.Content;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.OS;
using Android.Runtime;
using AndroidRect = Android.Graphics.Rect;
using AndroidSize = Android.Util.Size;
using ImageFormatType = Android.Graphics.ImageFormatType;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed record CameraReportSection(string Title, string Body);

internal sealed record CameraReport(IReadOnlyList<CameraReportSection> Sections, string PlainText, int CameraCount);

internal static class CameraCapabilitiesReport
{
    private const int ListedSizeLimit = 10;
    private const int RequestedWidth = 3840;
    private const int RequestedHeight = 2160;

    public static CameraReport Read()
    {
        var context = global::Android.App.Application.Context;
        var manager = context.GetSystemService(Context.CameraService) as CameraManager
            ?? throw new InvalidOperationException("Android returned no CameraManager.");
        var publicIds = manager.GetCameraIdList().OrderBy(static id => id, StringComparer.Ordinal).ToArray();
        var sections = new List<CameraReportSection>
        {
            new("DEVICE", BuildDeviceSummary(publicIds.Length)),
            new("HOW THIS APP REQUESTS FRAMES", BuildAppRequestSummary()),
            new("CONCURRENT CAMERA SETS", BuildConcurrentSummary(manager))
        };

        var physicalToLogical = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var cameraId in publicIds)
        {
            var characteristics = manager.GetCameraCharacteristics(cameraId);
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                foreach (var physicalId in characteristics.PhysicalCameraIds ?? [])
                {
                    if (!physicalToLogical.TryGetValue(physicalId, out var logicalIds))
                    {
                        logicalIds = [];
                        physicalToLogical.Add(physicalId, logicalIds);
                    }

                    logicalIds.Add(cameraId);
                }
            }
        }
        foreach (var cameraId in publicIds)
        {
            sections.Add(new CameraReportSection(
                $"PUBLIC CAMERA {cameraId}",
                BuildCameraSummary(manager, cameraId, isPublic: true, logicalParents: [])));
        }

        foreach (var pair in physicalToLogical.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (publicIds.Contains(pair.Key, StringComparer.Ordinal))
            {
                continue;
            }

            sections.Add(new CameraReportSection(
                $"PHYSICAL-ONLY LENS {pair.Key}",
                BuildCameraSummary(manager, pair.Key, isPublic: false, pair.Value)));
        }

        sections.Add(new CameraReportSection("HOW TO READ THE RESULT", BuildLegend()));
        var plainText = string.Join(
            System.Environment.NewLine + System.Environment.NewLine,
            sections.Select(static section => $"=== {section.Title} ==={System.Environment.NewLine}{section.Body}"));
        return new CameraReport(sections, plainText, publicIds.Length);
    }

    private static string BuildDeviceSummary(int cameraCount) => string.Join(System.Environment.NewLine,
        $"Device: {Build.Manufacturer} {Build.Model}",
        $"Product: {Build.Product} / {Build.Device}",
        $"Android: {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt})",
        $"Public camera IDs: {cameraCount}",
        "Source: live Camera2 metadata from this device");

    private static string BuildAppRequestSummary() => string.Join(System.Environment.NewLine,
        $"ImageAnalysis target: {RequestedWidth}×{RequestedHeight} ({Megapixels(RequestedWidth, RequestedHeight)})",
        "Format: YUV_420_888",
        "Fallback: closest higher resolution, then closest lower",
        "Back-camera selection: CameraX default back camera",
        "Zoom: CameraX zoom ratio; >1× may crop digitally or switch a logical lens",
        "Important: a supported size is not a promise CameraX will select it when preview and analysis are bound together.");

    private static string BuildConcurrentSummary(CameraManager manager)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            return "Requires Android 11 / API 30 or newer.";
        }

        var combinations = manager.ConcurrentCameraIds;
        if (combinations is null || combinations.Count == 0)
        {
            return "None reported. Android does not promise that two public cameras can be opened together.";
        }

        var lines = new List<string>();
        var number = 1;
        foreach (var combination in combinations)
        {
            var ids = combination is null
                ? []
                : combination.Cast<string>().OrderBy(static id => id, StringComparer.Ordinal).ToArray();
            lines.Add($"Set {number++}: {string.Join(" + ", ids)}");
        }

        lines.Add("These are public camera-device combinations. Two physical lenses inside one logical camera are a separate, more restricted Camera2 feature.");
        return string.Join(System.Environment.NewLine, lines);
    }

    private static string BuildCameraSummary(
        CameraManager manager,
        string cameraId,
        bool isPublic,
        IEnumerable<string> logicalParents)
    {
        try
        {
            var c = manager.GetCameraCharacteristics(cameraId);
            var lines = new List<string>
            {
                $"Openable directly: {YesNo(isPublic)}",
                $"Lens facing: {ReadEnum<LensFacing>(c, CameraCharacteristics.LensFacing)}",
                $"Hardware level: {ReadEnum<InfoSupportedHardwareLevel>(c, CameraCharacteristics.InfoSupportedHardwareLevel)}",
                $"Sensor orientation: {ReadInteger(c, CameraCharacteristics.SensorOrientation)}°",
                $"Focal lengths: {ReadFloatArray(c, CameraCharacteristics.LensInfoAvailableFocalLengths, " mm")}",
                $"Apertures: {ReadFloatArray(c, CameraCharacteristics.LensInfoAvailableApertures, string.Empty)}",
                $"Physical sensor: {ReadObject(c, CameraCharacteristics.SensorInfoPhysicalSize, " mm")}",
                $"Pixel array: {FormatSize(ReadObject<AndroidSize>(c, CameraCharacteristics.SensorInfoPixelArraySize))}",
                $"Active array: {FormatRect(ReadObject<AndroidRect>(c, CameraCharacteristics.SensorInfoActiveArraySize))}",
                $"Zoom ratio range: {(OperatingSystem.IsAndroidVersionAtLeast(30) ? ReadObject(c, CameraCharacteristics.ControlZoomRatioRange) : "requires API 30")}",
                $"Maximum digital zoom: {ReadFloat(c, CameraCharacteristics.ScalerAvailableMaxDigitalZoom)}×"
            };

            var physicalIds = OperatingSystem.IsAndroidVersionAtLeast(28)
                ? c.PhysicalCameraIds?.OrderBy(static id => id, StringComparer.Ordinal).ToArray() ?? []
                : [];
            lines.Add($"Physical lenses behind this ID: {(physicalIds.Length == 0 ? "none reported" : string.Join(", ", physicalIds))}");
            if (!isPublic)
            {
                lines.Add($"Logical parent(s): {string.Join(", ", logicalParents.OrderBy(static id => id, StringComparer.Ordinal))}");
            }

            var normalMap = ReadObject<StreamConfigurationMap>(c, CameraCharacteristics.ScalerStreamConfigurationMap);
            lines.Add(string.Empty);
            lines.Add("NORMAL YUV OUTPUTS (largest first)");
            lines.Add(FormatYuvSizes(normalMap, maximumResolution: false));
            lines.Add(string.Empty);
            lines.Add("NORMAL JPEG PHOTO OUTPUTS");
            lines.Add(FormatPhotoSizes(normalMap));

            var maximumMap = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? ReadObject<StreamConfigurationMap>(c, CameraCharacteristics.ScalerStreamConfigurationMapMaximumResolution)
                : null;
            var maximumPixelArray = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? ReadObject<AndroidSize>(c, CameraCharacteristics.SensorInfoPixelArraySizeMaximumResolution)
                : null;
            lines.Add(string.Empty);
            lines.Add("MAXIMUM-RESOLUTION SENSOR MODE");
            lines.Add($"Max pixel array: {FormatSize(maximumPixelArray)}");
            lines.Add("YUV frames:");
            lines.Add(FormatYuvSizes(maximumMap, maximumResolution: true));
            lines.Add("JPEG photos:");
            lines.Add(FormatPhotoSizes(maximumMap));
            return string.Join(System.Environment.NewLine, lines);
        }
        catch (Exception exception)
        {
            return $"Android advertised this ID but rejected its detailed metadata: {exception.Message}";
        }
    }

    private static string FormatYuvSizes(StreamConfigurationMap? map, bool maximumResolution)
    {
        if (map is null)
        {
            return maximumResolution
                ? "Not exposed. The sensor may still advertise a larger photo count while requiring pixel binning for app-accessible YUV frames."
                : "No YUV stream map exposed.";
        }

        var sizes = map.GetOutputSizes((int)ImageFormatType.Yuv420888) ?? [];
        if (sizes.Length == 0)
        {
            return "No YUV_420_888 sizes reported in this mode.";
        }

        var ordered = sizes
            .OrderByDescending(static size => (long)size.Width * size.Height)
            .ThenByDescending(static size => size.Width)
            .ToArray();
        var lines = new List<string>
        {
            $"Largest: {FormatSize(ordered[0])}",
            $"4K request available exactly: {YesNo(ordered.Any(static size => size.Width == RequestedWidth && size.Height == RequestedHeight))}",
            $"Top {Math.Min(ListedSizeLimit, ordered.Length)} of {ordered.Length}:"
        };
        foreach (var size in ordered.Take(ListedSizeLimit))
        {
            var duration = map.GetOutputMinFrameDuration((int)ImageFormatType.Yuv420888, size);
            var fps = duration > 0 ? $", max ≈{1_000_000_000d / duration:0.#} fps" : string.Empty;
            lines.Add($"  {FormatSize(size)}{fps}");
        }

        return string.Join(System.Environment.NewLine, lines);
    }

    private static string FormatPhotoSizes(StreamConfigurationMap? map)
    {
        if (map is null)
        {
            return "Not exposed in this sensor mode.";
        }

        var sizes = map.GetOutputSizes((int)ImageFormatType.Jpeg) ?? [];
        if (sizes.Length == 0)
        {
            return "No JPEG sizes reported in this sensor mode.";
        }

        var ordered = sizes
            .OrderByDescending(static size => (long)size.Width * size.Height)
            .ThenByDescending(static size => size.Width)
            .ToArray();
        return $"Largest: {FormatSize(ordered[0])}{System.Environment.NewLine}" +
            $"Top {Math.Min(5, ordered.Length)} of {ordered.Length}: " +
            string.Join(", ", ordered.Take(5).Select(FormatSize));
    }

    private static string BuildLegend() => string.Join(System.Environment.NewLine,
        "• Normal YUV outputs are the practical candidates for continuous frame analysis.",
        "• Maximum-resolution mode is special and may be much slower or incompatible with preview and a second camera.",
        "• 50 MP photo support does not imply 50 MP continuous YUV frames.",
        "• A concurrent set proves only that those public IDs can run together with constrained streams—not that both deliver their largest size.",
        "• Physical IDs behind a logical rear camera explain automatic lens switching. Forcing one physical lens requires a lower-level Camera2 capture path.");

    private static string ReadEnum<TEnum>(CameraCharacteristics characteristics, CameraCharacteristics.Key? key)
        where TEnum : struct, Enum
    {
        var value = key is null ? null : ReadObject<Java.Lang.Integer>(characteristics, key);
        return value is null
            ? "not reported"
            : ((TEnum)Enum.ToObject(typeof(TEnum), value.IntValue())).ToString();
    }

    private static string ReadInteger(CameraCharacteristics characteristics, CameraCharacteristics.Key? key) =>
        key is null ? "not reported" :
        ReadObject<Java.Lang.Integer>(characteristics, key)?.IntValue().ToString(CultureInfo.InvariantCulture) ?? "not reported";

    private static string ReadFloat(CameraCharacteristics characteristics, CameraCharacteristics.Key? key) =>
        key is null ? "not reported" :
        ReadObject<Java.Lang.Float>(characteristics, key)?.FloatValue().ToString("0.###", CultureInfo.InvariantCulture) ?? "not reported";

    private static string ReadFloatArray(CameraCharacteristics characteristics, CameraCharacteristics.Key? key, string suffix)
    {
        if (key is null)
        {
            return "not reported";
        }

        using var value = characteristics.Get(key);
        if (value is null)
        {
            return "not reported";
        }

#pragma warning disable CS0618 // Primitive arrays returned by CameraCharacteristics.Get require JNI conversion.
        var values = JNIEnv.GetArray<float>(value.Handle);
#pragma warning restore CS0618
        return values is null || values.Length == 0
            ? "not reported"
            : string.Join(", ", values.Select(item => item.ToString("0.###", CultureInfo.InvariantCulture) + suffix));
    }

    private static string ReadObject(CameraCharacteristics characteristics, CameraCharacteristics.Key? key, string suffix = "") =>
        key is not null && characteristics.Get(key)?.ToString() is { } value
            ? value + suffix
            : "not reported";

    private static T? ReadObject<T>(CameraCharacteristics characteristics, CameraCharacteristics.Key? key)
        where T : Java.Lang.Object => key is null ? null : characteristics.Get(key) as T;

    private static string FormatSize(AndroidSize? size) => size is null
        ? "not reported"
        : $"{size.Width}×{size.Height} ({Megapixels(size.Width, size.Height)})";

    private static string FormatRect(AndroidRect? rect) => rect is null
        ? "not reported"
        : $"{rect.Width()}×{rect.Height()}";

    private static string Megapixels(int width, int height) =>
        $"{(long)width * height / 1_000_000d:0.##} MP";

    private static string YesNo(bool value) => value ? "YES" : "no";
}
