using DeveMobileLPR.Inference.Onnx;

namespace DeveMobileLPR.Tests;

public sealed class OnnxProfileSummaryTests
{
    [Fact]
    public void ReadAndDelete_GroupsNodeDurationsPerProfiledRun()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"onnx-profile-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(
                path,
                """
                [
                  { "cat": "Node", "dur": 1000, "name": "conv_1_kernel_time", "args": { "op_name": "Conv" } },
                  { "cat": "Node", "dur": 3000, "name": "conv_2_kernel_time", "args": { "op_name": "Conv" } },
                  { "cat": "Node", "dur": 2000, "name": "transpose_kernel_time", "args": { "op_name": "Transpose" } },
                  { "cat": "Session", "dur": 999999, "name": "model_run" }
                ]
                """);

            var summary = OnnxProfileSummary.ReadAndDelete(path, profiledRunCount: 2, topOperationCount: 2);

            Assert.Equal("ONNX profile: top operations averaged across 2 runs", summary[0]);
            Assert.Equal("Conv: 2.0 ms/run · 1 calls/run", summary[1]);
            Assert.Equal("Transpose: 1.0 ms/run · 0.5 calls/run", summary[2]);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadAndDelete_ReportsMalformedTraceWithoutRetainingIt()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"onnx-profile-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{}");

            var summary = OnnxProfileSummary.ReadAndDelete(path, profiledRunCount: 1, topOperationCount: 1);

            Assert.Single(summary);
            Assert.Contains("unexpected trace format", summary[0], StringComparison.Ordinal);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
