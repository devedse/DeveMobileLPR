namespace DeveMobileLPR.Streaming;

public sealed class HlsCompletedSegmentCursor(int maximumRememberedUris = 256)
{
    private readonly int _maximumRememberedUris = maximumRememberedUris > 0
        ? maximumRememberedUris
        : throw new ArgumentOutOfRangeException(nameof(maximumRememberedUris));
    private readonly HashSet<string> _consumedUris = new(StringComparer.Ordinal);
    private readonly Queue<string> _consumedUriOrder = new();
    private long? _lastSequenceNumber;
    private bool _initialized;

    public HlsMediaSegment? SelectNext(HlsPlaylistSnapshot playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (playlist.Kind != HlsPlaylistKind.Media)
        {
            throw new ArgumentException("A completed-segment cursor requires an HLS media playlist.", nameof(playlist));
        }

        var segments = playlist.Segments
            .Where(static segment => segment.InitializationUri is not null)
            .ToArray();
        if (segments.Length == 0)
        {
            return null;
        }

        if (!_initialized)
        {
            _initialized = true;
            foreach (var segment in segments)
            {
                RememberUri(segment.Uri);
            }
            var latest = segments[^1];
            _lastSequenceNumber = playlist.MediaSequence.HasValue ? latest.SequenceNumber : null;
            return latest;
        }

        if (_lastSequenceNumber is { } lastSequence && playlist.MediaSequence.HasValue)
        {
            var latestSequence = segments[^1].SequenceNumber;
            if (latestSequence < lastSequence)
            {
                var latest = segments[^1];
                _lastSequenceNumber = latest.SequenceNumber;
                RememberUri(latest.Uri);
                return latest;
            }

            var next = segments.FirstOrDefault(segment => segment.SequenceNumber > lastSequence);
            if (next is not null)
            {
                _lastSequenceNumber = next.SequenceNumber;
                RememberUri(next.Uri);
            }
            return next;
        }

        return segments.FirstOrDefault(segment => RememberUri(segment.Uri));
    }

    private bool RememberUri(Uri uri)
    {
        if (!_consumedUris.Add(uri.AbsoluteUri))
        {
            return false;
        }

        _consumedUriOrder.Enqueue(uri.AbsoluteUri);
        while (_consumedUriOrder.Count > _maximumRememberedUris)
        {
            _consumedUris.Remove(_consumedUriOrder.Dequeue());
        }
        return true;
    }
}
