using DeveMobileLPR.Inference.Onnx;

namespace DeveMobileLPR.Tests;

public sealed class NnapiSessionOptionsBridgeTests
{
    [Fact]
    public void VerifyStatusHandlingContract_WithCurrentOnnxRuntimePackage_Succeeds()
    {
        NnapiSessionOptionsBridge.VerifyStatusHandlingContract();
    }
}
