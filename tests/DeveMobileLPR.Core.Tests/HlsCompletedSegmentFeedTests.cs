using System.Net;
using DeveMobileLPR.Streaming;

namespace DeveMobileLPR.Tests;

public sealed class HlsCompletedSegmentFeedTests
{
    private static readonly Uri EntryUri = new("https://media.example.test/live/llhls.m3u8");

    [Fact]
    public async Task GetNextAsync_AcceptsDirectMediaPlaylistAndArbitrarySegmentName()
    {
        var client = CreateClient(_ => """
            #EXTM3U
            #EXT-X-TARGETDURATION:2
            #EXT-X-MEDIA-SEQUENCE:7
            #EXT-X-MAP:URI="init.mp4"
            #EXTINF:2,
            completed-camera-fragment.m4s
            """);
        var feed = new HlsCompletedSegmentFeed(EntryUri, client);

        var next = await feed.GetNextAsync(CancellationToken.None);

        Assert.Equal(new Uri("https://media.example.test/live/init.mp4"), next.Initialization);
        Assert.Equal(new Uri("https://media.example.test/live/completed-camera-fragment.m4s"), next.Media);
    }

    [Fact]
    public async Task GetNextAsync_SelectsHighestResolutionVideoVariantFromMaster()
    {
        var requested = new List<Uri>();
        var client = CreateClient(uri =>
        {
            requested.Add(uri);
            return uri == EntryUri
                ? """
                    #EXTM3U
                    #EXT-X-STREAM-INF:BANDWIDTH=100000,RESOLUTION=640x360,CODECS="avc1.4d401e"
                    low.m3u8
                    #EXT-X-STREAM-INF:BANDWIDTH=200000,RESOLUTION=1280x720,CODECS="avc1.64001f"
                    high.m3u8
                    """
                : """
                    #EXTM3U
                    #EXT-X-TARGETDURATION:2
                    #EXT-X-MAP:URI="init.mp4"
                    #EXTINF:2,
                    segment.m4s
                    """;
        });
        var feed = new HlsCompletedSegmentFeed(EntryUri, client);

        _ = await feed.GetNextAsync(CancellationToken.None);

        Assert.Equal(
            [EntryUri, new Uri("https://media.example.test/live/high.m3u8")],
            requested);
    }

    [Fact]
    public async Task GetNextAsync_CancelsWhileWaitingForFirstCompletedSegment()
    {
        var client = CreateClient(_ => """
            #EXTM3U
            #EXT-X-TARGETDURATION:2
            #EXT-X-MAP:URI="init.mp4"
            #EXT-X-PART:DURATION=0.2,URI="part.m4s"
            """);
        var feed = new HlsCompletedSegmentFeed(EntryUri, client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => feed.GetNextAsync(cancellation.Token));
    }

    [Fact]
    public async Task GetNextAsync_RejectsNonFragmentedMp4Playlist()
    {
        var client = CreateClient(_ => """
            #EXTM3U
            #EXT-X-TARGETDURATION:2
            #EXTINF:2,
            segment.ts
            """);
        var feed = new HlsCompletedSegmentFeed(EntryUri, client);

        await Assert.ThrowsAsync<NotSupportedException>(() => feed.GetNextAsync(CancellationToken.None));
    }

    private static HttpClient CreateClient(Func<Uri, string> response) => new(new StubHandler(response));

    private sealed class StubHandler(Func<Uri, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response(request.RequestUri!))
            };
            return Task.FromResult(message);
        }
    }
}
