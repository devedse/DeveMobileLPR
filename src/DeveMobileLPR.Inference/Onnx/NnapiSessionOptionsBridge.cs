using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace DeveMobileLPR.Inference.Onnx;

internal static class NnapiSessionOptionsBridge
{
    private const string OnnxRuntimeLibrary = "libonnxruntime.so";
    private static readonly Action<IntPtr> VerifySuccess = CreateStatusVerifier();

    public static void AppendExecutionProvider(SessionOptions options, NnapiFlags flags)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsAndroid())
        {
            throw new PlatformNotSupportedException("The NNAPI execution provider is only available on Android.");
        }

        var status = OrtSessionOptionsAppendExecutionProviderNnapi(
            options.DangerousGetHandle(),
            (uint)flags);
        VerifySuccess(status);
    }

    internal static void VerifyStatusHandlingContract()
    {
        VerifySuccess(IntPtr.Zero);
    }

    private static Action<IntPtr> CreateStatusVerifier()
    {
        const string statusTypeName = "Microsoft.ML.OnnxRuntime.NativeApiStatus";
        var statusType = typeof(SessionOptions).Assembly.GetType(statusTypeName, throwOnError: true)
            ?? throw new TypeLoadException(statusTypeName);
        var verifySuccess = statusType.GetMethod(
            "VerifySuccess",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(IntPtr)],
            modifiers: null)
            ?? throw new MissingMethodException(statusTypeName, "VerifySuccess(IntPtr)");
        return verifySuccess.CreateDelegate<Action<IntPtr>>();
    }

    [DllImport(
        OnnxRuntimeLibrary,
        EntryPoint = "OrtSessionOptionsAppendExecutionProvider_Nnapi",
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    private static extern IntPtr OrtSessionOptionsAppendExecutionProviderNnapi(
        IntPtr options,
        uint flags);
}
