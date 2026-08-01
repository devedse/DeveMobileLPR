using DeveMobileLPR.Streaming;

namespace DeveMobileLPR.Tests;

public sealed class NetworkVideoStreamTests
{
    [Theory]
    [InlineData("https://media.example.test/app/camera/llhls.m3u8")]
    [InlineData("http://media.example.test:3333/app/camera/master.m3u8?token=temporary")]
    public void TryParse_AcceptsHttpHlsPlaylist(string value)
    {
        var parsed = NetworkVideoStream.TryParse(value, out var stream);

        Assert.True(parsed);
        Assert.NotNull(stream);
        Assert.Equal(NetworkVideoProtocol.LowLatencyHls, stream.Protocol);
        Assert.Equal(value, stream.Uri.AbsoluteUri);
    }

    [Fact]
    public void TryParse_ConvertsDspShareLinkToUnlistedLowLatencyPlaylist()
    {
        const string shareLink = "https://dsp.media.example.test/s/camera__ul__0123456789abcdef";

        var parsed = NetworkVideoStream.TryParse(shareLink, out var stream);

        Assert.True(parsed);
        Assert.NotNull(stream);
        Assert.Equal(
            "https://media.example.test:3334/unlisted/camera__ul__0123456789abcdef/multistream_llhls.m3u8",
            stream.Uri.AbsoluteUri);
    }

    [Fact]
    public void TryParse_ConvertsShareLinkWithoutHardCodingTheDomain()
    {
        const string shareLink = "https://dsp.another-host.example/s/stream-id?access=temporary#ignore";

        var parsed = NetworkVideoStream.TryParse(shareLink, out var stream);

        Assert.True(parsed);
        Assert.NotNull(stream);
        Assert.Equal(
            "https://another-host.example:3334/unlisted/stream-id/multistream_llhls.m3u8?access=temporary",
            stream.Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("camera/llhls.m3u8")]
    [InlineData("srt://media.example.test:9999?streamid=camera")]
    [InlineData("https://media.example.test/app/camera/webrtc")]
    [InlineData("https://dsp.media.example.test/not-a-share-link/camera")]
    [InlineData("https://dsp.media.example.test/s/one/too-many")]
    public void TryParse_RejectsUnsupportedInput(string? value)
    {
        Assert.False(NetworkVideoStream.TryParse(value, out var stream));
        Assert.Null(stream);
    }
}
