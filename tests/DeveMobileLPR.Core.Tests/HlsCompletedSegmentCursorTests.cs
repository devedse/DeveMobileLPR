using DeveMobileLPR.Streaming;

namespace DeveMobileLPR.Tests;

public sealed class HlsCompletedSegmentCursorTests
{
    private static readonly Uri PlaylistUri = new("https://media.example.test/live/video.m3u8");
    private static readonly Uri InitializationUri = new("https://media.example.test/live/init.mp4");

    [Fact]
    public void SelectNext_StartsAtLiveEdgeThenAdvancesBySequence()
    {
        var cursor = new HlsCompletedSegmentCursor();

        var initial = cursor.SelectNext(CreatePlaylist(100, 3));
        var unchanged = cursor.SelectNext(CreatePlaylist(100, 3));
        var advanced = cursor.SelectNext(CreatePlaylist(101, 3));

        Assert.Equal(102, initial?.SequenceNumber);
        Assert.Null(unchanged);
        Assert.Equal(103, advanced?.SequenceNumber);
    }

    [Fact]
    public void SelectNext_ResumesAtLiveEdgeAfterServerSequenceReset()
    {
        var cursor = new HlsCompletedSegmentCursor();
        _ = cursor.SelectNext(CreatePlaylist(100, 3));

        var reset = cursor.SelectNext(CreatePlaylist(0, 2));

        Assert.Equal(1, reset?.SequenceNumber);
    }

    [Fact]
    public void SelectNext_UsesBoundedUriHistoryWhenMediaSequenceIsAbsent()
    {
        var cursor = new HlsCompletedSegmentCursor(2);
        var firstPlaylist = CreatePlaylist(null, 2, 0);
        _ = cursor.SelectNext(firstPlaylist);

        var next = cursor.SelectNext(CreatePlaylist(null, 2, 2));

        Assert.Equal("segment-2.m4s", Path.GetFileName(next?.Uri.AbsolutePath));
    }

    private static HlsPlaylistSnapshot CreatePlaylist(long? mediaSequence, int count, int uriOffset = 0)
    {
        var sequenceBase = mediaSequence ?? 0;
        var segments = Enumerable.Range(0, count)
            .Select(index => new HlsMediaSegment(
                sequenceBase + index,
                new Uri(PlaylistUri, $"segment-{uriOffset + index}.m4s"),
                InitializationUri,
                TimeSpan.FromSeconds(2)))
            .ToArray();
        return new HlsPlaylistSnapshot(HlsPlaylistKind.Media, PlaylistUri, mediaSequence, [], segments);
    }
}
