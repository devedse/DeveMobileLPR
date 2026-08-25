using DeveMobileLPR.Application;

namespace DeveMobileLPR.Tests;

public sealed class DriveInputConfigurationPlannerTests
{
    [Fact]
    public void Create_NormalizesKnownSourcesWithoutImposingACameraCount()
    {
        var capabilities = new[]
        {
            Physical("physical:0:main", "main", 1f, 4f),
            Physical("physical:0:tele", "tele", 1f, 8f),
            Physical("physical:0:extra", "extra", 1f, 2f)
        };
        var configuration = new DriveInputConfiguration(
            DriveInputConfiguration.CurrentVersion,
            DriveInputMode.Multi,
            [
                new("physical:0:main", true, new VideoResolution(1920, 1080), 9f),
                new("physical:0:tele", true, new VideoResolution(3840, 2160), 2f),
                new("physical:0:extra", true, new VideoResolution(1280, 720), 1f)
            ]);

        var plan = DriveInputConfigurationPlanner.Create(configuration, capabilities, true);

        Assert.Equal(3, plan.EnabledSources.Count);
        Assert.Equal(4f, plan.EnabledSources[0].Profile.Zoom);
        Assert.Equal(new VideoResolution(3840, 2160), plan.EnabledSources[0].Profile.Resolution);
    }

    [Fact]
    public void Create_RejectsMultiModeWhenThePlatformDoesNotSupportIt()
    {
        var capability = Physical("camera", "camera", 1f, 4f);
        var configuration = new DriveInputConfiguration(
            DriveInputConfiguration.CurrentVersion,
            DriveInputMode.Multi,
            [new("camera", true, new VideoResolution(3840, 2160), 1f)]);

        Assert.Throws<NotSupportedException>(() =>
            DriveInputConfigurationPlanner.Create(configuration, [capability], false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///camera.m3u8")]
    public void Create_RejectsInvalidNetworkUrls(string? url)
    {
        var capability = new DriveSourceCapability(
            DriveInputIds.NetworkLlHls,
            "Network",
            DriveSourceKind.NetworkLlHls,
            false,
            null,
            null,
            null,
            null,
            null,
            1f,
            1f,
            []);
        var configuration = new DriveInputConfiguration(
            DriveInputConfiguration.CurrentVersion,
            DriveInputMode.Single,
            [new(DriveInputIds.NetworkLlHls, true, new VideoResolution(1920, 1080), 1f, url)],
            DriveInputIds.NetworkLlHls);

        Assert.Throws<InvalidOperationException>(() =>
            DriveInputConfigurationPlanner.Create(configuration, [capability], true));
    }

    private static DriveSourceCapability Physical(
        string id,
        string physicalId,
        float minimumZoom,
        float maximumZoom) =>
        new(
            id,
            physicalId,
            DriveSourceKind.PhysicalCamera,
            true,
            "0",
            physicalId,
            null,
            null,
            null,
            minimumZoom,
            maximumZoom,
            [new VideoResolution(3840, 2160)]);
}
