using System.Net;

namespace DeveMobileLPR.Streaming;

public sealed class HlsCompletedSegmentFeed
{
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });
    private static readonly TimeSpan PlaylistPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Uri _entryUri;
    private readonly HttpClient _client;
    private readonly HlsCompletedSegmentCursor _cursor = new();
    private Uri? _mediaPlaylistUri;

    public HlsCompletedSegmentFeed(Uri entryUri, HttpClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(entryUri);
        if (!entryUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The HLS entry URI must be absolute.", nameof(entryUri));
        }

        _entryUri = entryUri;
        _client = client ?? SharedClient;
    }

    public async Task<(Uri Initialization, Uri Media)> GetNextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var playlist = await GetMediaPlaylistAsync(cancellationToken).ConfigureAwait(false);
            if (playlist.Segments.Count > 0 && playlist.Segments.All(static segment => segment.InitializationUri is null))
            {
                throw new NotSupportedException("The HLS stream is not fragmented MP4: its media playlist has no EXT-X-MAP initialization section.");
            }

            var next = _cursor.SelectNext(playlist);
            if (next is not null)
            {
                return (next.InitializationUri!, next.Uri);
            }

            await Task.Delay(PlaylistPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public void SkipToLiveEdge() => _cursor.SkipToLiveEdge();

    private async Task<HlsPlaylistSnapshot> GetMediaPlaylistAsync(CancellationToken cancellationToken)
    {
        var playlistUri = _mediaPlaylistUri ?? _entryUri;
        var playlist = await DownloadPlaylistAsync(playlistUri, cancellationToken).ConfigureAwait(false);
        if (playlist.Kind == HlsPlaylistKind.Media)
        {
            _mediaPlaylistUri = playlistUri;
            return playlist;
        }

        var selectedVariant = playlist.SelectBestVideoVariant();
        _mediaPlaylistUri = selectedVariant.Uri;
        var selectedPlaylist = await DownloadPlaylistAsync(selectedVariant.Uri, cancellationToken).ConfigureAwait(false);
        return selectedPlaylist.Kind == HlsPlaylistKind.Media
            ? selectedPlaylist
            : throw new InvalidDataException("The selected HLS variant does not reference a media playlist.");
    }

    private async Task<HlsPlaylistSnapshot> DownloadPlaylistAsync(Uri uri, CancellationToken cancellationToken)
    {
        var text = await _client.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
        return HlsPlaylistParser.Parse(uri, text);
    }
}
