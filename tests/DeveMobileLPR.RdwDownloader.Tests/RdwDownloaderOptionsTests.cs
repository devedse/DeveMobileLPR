using DeveMobileLPR.RdwDownloader;

namespace DeveMobileLPR.Tests;

public sealed class RdwDownloaderOptionsTests
{
    [Fact]
    public void Parse_UsesSafeDefaultsAndEnvironmentToken()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"rdw-options-{Guid.NewGuid():N}"));

        var options = RdwDownloaderOptions.Parse([], root, " environment-token ");

        Assert.Equal(Path.Combine(root, "artifacts", "rdw", "rdw.sqlite"), options.OutputPath);
        Assert.Equal("environment-token", options.AppToken);
        Assert.Equal(50_000, options.PageSize);
        Assert.Null(options.SampleRows);
        Assert.False(options.Restart);
    }

    [Fact]
    public void Parse_HandlesEveryCommandLineOptionAndOverridesEnvironmentToken()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"rdw-options-{Guid.NewGuid():N}"));

        var options = RdwDownloaderOptions.Parse(
            ["-o", "custom.sqlite", "--page-size", "1234", "--sample-rows", "25", "--app-token", "cli-token", "--restart"],
            root,
            "environment-token");

        Assert.Equal(Path.Combine(root, "custom.sqlite"), options.OutputPath);
        Assert.Equal("cli-token", options.AppToken);
        Assert.Equal(1_234, options.PageSize);
        Assert.Equal(25, options.SampleRows);
        Assert.True(options.Restart);
    }

    [Theory]
    [InlineData("--page-size", "0")]
    [InlineData("--page-size", "50001")]
    [InlineData("--sample-rows", "0")]
    [InlineData("--unknown", "value")]
    public void Parse_RejectsInvalidArguments(string option, string value) =>
        Assert.Throws<ArgumentException>(() => RdwDownloaderOptions.Parse([option, value], Path.GetTempPath(), null));
}
