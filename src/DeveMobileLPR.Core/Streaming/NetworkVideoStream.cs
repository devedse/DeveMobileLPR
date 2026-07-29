namespace DeveMobileLPR.Streaming;

public enum NetworkVideoProtocol
{
    LowLatencyHls
}

public sealed record NetworkVideoStream(Uri Uri, NetworkVideoProtocol Protocol)
{
    public static bool TryParse(string? value, out NetworkVideoStream? stream)
    {
        stream = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        stream = new NetworkVideoStream(uri, NetworkVideoProtocol.LowLatencyHls);
        return true;
    }
}