using DeveMobileLPR.Streaming;

namespace DeveMobileLPR.Tests;

public sealed class HlsPlaylistParserTests
{
    private static readonly Uri MasterUri = new("https://media.example.test/live/camera/llhls.m3u8");

    [Fact]
    public void Parse_MasterPlaylist_ResolvesAndSelectsHighestResolutionVideoVariant()
    {
        const string content = """
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2"
            low/playlist.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=3500000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
            high/playlist.m3u8
            """;

        var playlist = HlsPlaylistParser.Parse(MasterUri, content);

        Assert.Equal(HlsPlaylistKind.Master, playlist.Kind);
        Assert.Equal(2, playlist.Variants.Count);
        var selected = playlist.SelectBestVideoVariant();
        Assert.Equal(new Uri("https://media.example.test/live/camera/high/playlist.m3u8"), selected.Uri);
        Assert.Equal(1920, selected.Width);
        Assert.Equal(1080, selected.Height);
    }

    [Fact]
    public void Parse_MediaPlaylist_TracksSequenceAndInitializationPerSegment()
    {
        const string content = """
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:42
            #EXT-X-MAP:URI="init-a.mp4"
            #EXTINF:6.0,
            camera-42.m4s
            #EXT-X-DISCONTINUITY
            #EXT-X-MAP:URI="init-b.mp4"
            #EXTINF:5.5,
            camera-43.m4s?token=segment
            """;

        var playlist = HlsPlaylistParser.Parse(MasterUri, content);

        Assert.Equal(HlsPlaylistKind.Media, playlist.Kind);
        Assert.Equal(42, playlist.MediaSequence);
        Assert.Collection(
            playlist.Segments,
            segment =>
            {
                Assert.Equal(42, segment.SequenceNumber);
                Assert.Equal(new Uri("https://media.example.test/live/camera/camera-42.m4s"), segment.Uri);
                Assert.Equal(new Uri("https://media.example.test/live/camera/init-a.mp4"), segment.InitializationUri);
                Assert.Equal(TimeSpan.FromSeconds(6), segment.Duration);
            },
            segment =>
            {
                Assert.Equal(43, segment.SequenceNumber);
                Assert.Equal(new Uri("https://media.example.test/live/camera/camera-43.m4s?token=segment"), segment.Uri);
                Assert.Equal(new Uri("https://media.example.test/live/camera/init-b.mp4"), segment.InitializationUri);
                Assert.Equal(TimeSpan.FromSeconds(5.5), segment.Duration);
            });
    }

    [Fact]
    public void Parse_MediaPlaylist_DoesNotDependOnOmeSegmentFileNames()
    {
        const string content = """
            #EXTM3U
            #EXT-X-TARGETDURATION:2
            #EXT-X-MAP:URI="header.mp4"
            #EXTINF:2,
            arbitrary-completed-fragment.m4s
            """;

        var playlist = HlsPlaylistParser.Parse(MasterUri, content);

        var segment = Assert.Single(playlist.Segments);
        Assert.Equal("arbitrary-completed-fragment.m4s", Path.GetFileName(segment.Uri.AbsolutePath));
    }

    [Theory]
    [InlineData("#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\",BYTERANGE=\"100@0\"\n#EXTINF:2,\nsegment.m4s")]
    [InlineData("#EXTM3U\n#EXT-X-BYTERANGE:100@0\n#EXTINF:2,\nsegment.m4s")]
    [InlineData("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXTINF:2,\nsegment.m4s")]
    public void Parse_RejectsUnsupportedMediaFeaturesExplicitly(string content)
    {
        Assert.Throws<NotSupportedException>(() => HlsPlaylistParser.Parse(MasterUri, content));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a playlist")]
    [InlineData("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000")]
    [InlineData("#EXTM3U\n#EXTINF:2")]
    public void Parse_RejectsMalformedPlaylist(string content)
    {
        Assert.Throws<InvalidDataException>(() => HlsPlaylistParser.Parse(MasterUri, content));
    }
}
