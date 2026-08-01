namespace DeveMobileLPR.Streaming;

public enum NetworkVideoProtocol
{
    LowLatencyHls
}

public sealed record NetworkVideoStream(Uri Uri, NetworkVideoProtocol Protocol)
{
    private const string ShareHostPrefix = "dsp.";
    private const string SharePathSegment = "s";
    private const string UnlistedStreamPathSegment = "unlisted";
    private const string LowLatencyPlaylistName = "multistream_llhls.m3u8";
    private const int LowLatencyStreamingPort = 3334;

    public static bool TryParse(string? value, out NetworkVideoStream? stream)
    {
        stream = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            && !TryConvertShareLink(uri, out uri))
        {
            return false;
        }

        stream = new NetworkVideoStream(uri, NetworkVideoProtocol.LowLatencyHls);
        return true;
    }

    private static bool TryConvertShareLink(Uri shareUri, out Uri playlistUri)
    {
        playlistUri = shareUri;
        if (!shareUri.Host.StartsWith(ShareHostPrefix, StringComparison.OrdinalIgnoreCase)
            || shareUri.Host.Length == ShareHostPrefix.Length)
        {
            return false;
        }

        var pathSegments = shareUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Length != 2
            || !string.Equals(pathSegments[0], SharePathSegment, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var streamId = Uri.UnescapeDataString(pathSegments[1]);
        if (string.IsNullOrWhiteSpace(streamId)
            || streamId.Contains('/')
            || streamId.Contains('\\'))
        {
            return false;
        }

        var builder = new UriBuilder(shareUri)
        {
            Host = shareUri.Host[ShareHostPrefix.Length..],
            Port = LowLatencyStreamingPort,
            Path = $"/{UnlistedStreamPathSegment}/{Uri.EscapeDataString(streamId)}/{LowLatencyPlaylistName}",
            Fragment = string.Empty
        };
        playlistUri = builder.Uri;
        return true;
    }
}
