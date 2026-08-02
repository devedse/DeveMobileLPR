using DeveMobileLPR.Inference.Onnx;

namespace DeveMobileLPR.App.Services;

/// <summary>
/// Selects the statically linked WebGPU provider in ONNX Runtime's Android AAR.
/// Dawn uses Vulkan on Android, so this remains vendor-neutral across GPUs.
/// </summary>
internal static class AndroidWebGpuExecutionProvider
{
    public static IReadOnlyList<OnnxExecutionProviderConfiguration> CreateCandidates(
        bool compareConfigurations)
    {
        if (!compareConfigurations)
        {
            return [Create("NCHW", enableGraphCapture: true)];
        }

        return
        [
            Create("NCHW", enableGraphCapture: false),
            Create("NCHW", enableGraphCapture: true),
            Create("NHWC", enableGraphCapture: false),
            Create("NHWC", enableGraphCapture: true)
        ];
    }

    private static OnnxExecutionProviderConfiguration Create(
        string preferredLayout,
        bool enableGraphCapture) =>
        new(
            $"WebGPU Vulkan {preferredLayout}{(enableGraphCapture ? " + capture" : string.Empty)}",
            options =>
            {
                // A WebGPU comparison build must not silently execute unsupported
                // detector nodes on the CPU while presenting itself as GPU-backed.
                options.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
                options.AppendExecutionProvider(
                    "WebGPU",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["preferredLayout"] = preferredLayout,
                        ["powerPreference"] = "high-performance",
                        ["enableGraphCapture"] = enableGraphCapture ? "1" : "0"
                    });
            });
}
