using System.Net;

namespace DeveMobileLPR.App.Platforms.Windows;

internal sealed class WindowsHlsCompletedSegmentFeed
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

    private readonly Uri _masterUri;
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    private Uri? _mediaPlaylistUri;
    private bool _initialized;

    public WindowsHlsCompletedSegmentFeed(Uri masterUri) => _masterUri = masterUri;

    public async Task<(Uri Initialization, Uri Media)> GetNextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var playlistUri = await GetMediaPlaylistUriAsync(cancellationToken).ConfigureAwait(false);
            var text = await Client.GetStringAsync(playlistUri, cancellationToken).ConfigureAwait(false);
            Uri? initialization = null;
            var segments = new List<Uri>();
            foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                {
                    initialization = ReadUriAttribute(line, playlistUri);
                }
                else if (line.Length > 0 && line[0] != '#' && line.Contains("seg_", StringComparison.Ordinal))
                {
                    segments.Add(new Uri(playlistUri, line));
                }
            }
            if (initialization is not null)
            {
                if (!_initialized && segments.Count > 0)
                {
                    _initialized = true;
                    foreach (var existing in segments)
                    {
                        _consumed.Add(existing.AbsoluteUri);
                    }
                    return (initialization, segments[^1]);
                }
                foreach (var segment in segments)
                {
                    if (_consumed.Add(segment.AbsoluteUri))
                    {
                        return (initialization, segment);
                    }
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Uri> GetMediaPlaylistUriAsync(CancellationToken cancellationToken)
    {
        if (_mediaPlaylistUri is not null)
        {
            return _mediaPlaylistUri;
        }
        var master = await Client.GetStringAsync(_masterUri, cancellationToken).ConfigureAwait(false);
        var variant = master.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.Length > 0 && line[0] != '#')
            ?? throw new InvalidDataException("The HLS master playlist contains no video variant.");
        _mediaPlaylistUri = new Uri(_masterUri, variant);
        return _mediaPlaylistUri;
    }

    private static Uri? ReadUriAttribute(string line, Uri playlistUri)
    {
        const string marker = "URI=\"";
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        start += marker.Length;
        var end = line.IndexOf('"', start);
        return end < 0 ? null : new Uri(playlistUri, line[start..end]);
    }
}
