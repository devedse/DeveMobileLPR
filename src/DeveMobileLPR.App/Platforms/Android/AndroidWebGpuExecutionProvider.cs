using DeveMobileLPR.Inference.Onnx;
using Microsoft.ML.OnnxRuntime;

namespace DeveMobileLPR.App.Services;

/// <summary>
/// Selects ONNX Runtime's native WebGPU provider on Android. The Android ONNX
/// Runtime AAR uses Dawn with Vulkan, so this is vendor-neutral across Android
/// GPU vendors.
/// </summary>
internal static class AndroidWebGpuExecutionProvider
{
    private const string ProviderName = "WebGpuExecutionProvider";

    public static OnnxExecutionProviderConfiguration? TryCreate(Action<string>? diagnostic)
    {
        try
        {
            var environment = OrtEnv.Instance();
            var devices = environment.GetEpDevices()
                .Where(device => string.Equals(device.EpName, ProviderName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (devices.Length == 0)
            {
                diagnostic?.Invoke("ONNX Runtime WebGPU provider is not present in the Android runtime.");
                return null;
            }

            diagnostic?.Invoke($"ONNX Runtime WebGPU provider exposes {devices.Length} device(s); Vulkan backend will be used when available.");
            return new OnnxExecutionProviderConfiguration(
                "WebGPU",
                options => options.AppendExecutionProvider(
                    environment,
                    devices,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        // The raw YOLO graph is channels-first and is not a candidate
                        // for an implicit layout conversion.
                        ["preferredLayout"] = "NCHW"
                    }));
        }
        catch (Exception exception)
        {
            diagnostic?.Invoke($"ONNX Runtime WebGPU provider unavailable: {exception.GetBaseException().Message}");
            return null;
        }
    }
}
