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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("camera/llhls.m3u8")]
    [InlineData("srt://media.example.test:9999?streamid=camera")]
    [InlineData("https://media.example.test/app/camera/webrtc")]
    public void TryParse_RejectsUnsupportedInput(string? value)
    {
        Assert.False(NetworkVideoStream.TryParse(value, out var stream));
        Assert.Null(stream);
    }
}