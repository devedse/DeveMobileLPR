using System.Globalization;

namespace DeveMobileLPR.Streaming;

public enum HlsPlaylistKind
{
    Master,
    Media
}

public sealed record HlsVariantStream(
    Uri Uri,
    int? Bandwidth,
    int? Width,
    int? Height,
    string? Codecs)
{
    private static readonly string[] VideoCodecPrefixes =
    [
        "avc1", "avc3", "hev1", "hvc1", "dvh1", "dvhe", "vp09", "av01"
    ];

    public bool HasVideo => Width is > 0 && Height is > 0
        || string.IsNullOrWhiteSpace(Codecs)
        || Codecs.Split(',').Select(static codec => codec.Trim()).Any(codec =>
            VideoCodecPrefixes.Any(prefix => codec.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    public long PixelCount => (long)(Width ?? 0) * (Height ?? 0);
}

public sealed record HlsMediaSegment(
    long SequenceNumber,
    Uri Uri,
    Uri? InitializationUri,
    TimeSpan Duration);

public sealed record HlsPlaylistSnapshot(
    HlsPlaylistKind Kind,
    Uri Uri,
    long? MediaSequence,
    IReadOnlyList<HlsVariantStream> Variants,
    IReadOnlyList<HlsMediaSegment> Segments)
{
    public HlsVariantStream SelectBestVideoVariant() => Variants
        .Where(static variant => variant.HasVideo)
        .OrderByDescending(static variant => variant.PixelCount)
        .ThenByDescending(static variant => variant.Bandwidth ?? 0)
        .FirstOrDefault()
        ?? throw new InvalidDataException("The HLS master playlist contains no video variant.");
}

public static class HlsPlaylistParser
{
    public static HlsPlaylistSnapshot Parse(Uri playlistUri, string content)
    {
        ArgumentNullException.ThrowIfNull(playlistUri);
        ArgumentNullException.ThrowIfNull(content);
        if (!playlistUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The playlist URI must be absolute.", nameof(playlistUri));
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToArray();
        if (lines.Length == 0 || !string.Equals(lines[0].TrimStart('\uFEFF'), "#EXTM3U", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The response is not an HLS playlist.");
        }

        var variants = new List<HlsVariantStream>();
        var segments = new List<HlsMediaSegment>();
        IReadOnlyDictionary<string, string>? pendingVariant = null;
        TimeSpan? pendingDuration = null;
        Uri? initializationUri = null;
        long? mediaSequence = null;
        var sawMediaTag = false;

        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                if (pendingVariant is not null)
                {
                    throw new InvalidDataException("An HLS variant is missing its playlist URI.");
                }
                pendingVariant = ParseAttributeList(ValueAfterColon(line));
                continue;
            }

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                sawMediaTag = true;
                if (segments.Count > 0
                    || !long.TryParse(ValueAfterColon(line), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSequence)
                    || parsedSequence < 0)
                {
                    throw new InvalidDataException("The HLS media sequence is invalid.");
                }
                mediaSequence = parsedSequence;
                continue;
            }

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                sawMediaTag = true;
                var attributes = ParseAttributeList(ValueAfterColon(line));
                if (attributes.ContainsKey("BYTERANGE"))
                {
                    throw new NotSupportedException("Byte-range HLS initialization sections are not supported.");
                }
                initializationUri = ResolveRequiredUri(attributes, "URI", playlistUri, "initialization section");
                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                sawMediaTag = true;
                if (pendingDuration is not null)
                {
                    throw new InvalidDataException("An HLS media segment is missing its URI.");
                }
                var value = ValueAfterColon(line).Split(',', 2)[0];
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSeconds)
                    || !double.IsFinite(durationSeconds)
                    || durationSeconds < 0)
                {
                    throw new InvalidDataException("The HLS media segment duration is invalid.");
                }
                pendingDuration = TimeSpan.FromSeconds(durationSeconds);
                continue;
            }

            if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                sawMediaTag = true;
                var attributes = ParseAttributeList(ValueAfterColon(line));
                if (!attributes.TryGetValue("METHOD", out var method)
                    || !string.Equals(method, "NONE", StringComparison.Ordinal))
                {
                    throw new NotSupportedException("Encrypted HLS media segments are not supported.");
                }
                continue;
            }

            if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                throw new NotSupportedException("Byte-range HLS media segments are not supported.");
            }

            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal)
                || line.StartsWith("#EXT-X-PART:", StringComparison.Ordinal)
                || line.StartsWith("#EXT-X-PART-INF:", StringComparison.Ordinal)
                || line.StartsWith("#EXT-X-PRELOAD-HINT:", StringComparison.Ordinal))
            {
                sawMediaTag = true;
                continue;
            }

            if (line[0] == '#')
            {
                continue;
            }

            var resolvedUri = ResolveUri(playlistUri, line, "playlist entry");
            if (pendingVariant is not null)
            {
                variants.Add(CreateVariant(resolvedUri, pendingVariant));
                pendingVariant = null;
            }
            else if (pendingDuration is not null)
            {
                var sequenceNumber = checked((mediaSequence ?? 0) + segments.Count);
                segments.Add(new HlsMediaSegment(sequenceNumber, resolvedUri, initializationUri, pendingDuration.Value));
                pendingDuration = null;
            }
            else
            {
                throw new InvalidDataException("The HLS playlist contains an unexpected URI.");
            }
        }

        if (pendingVariant is not null)
        {
            throw new InvalidDataException("An HLS variant is missing its playlist URI.");
        }
        if (pendingDuration is not null)
        {
            throw new InvalidDataException("An HLS media segment is missing its URI.");
        }
        if (variants.Count > 0 && sawMediaTag)
        {
            throw new InvalidDataException("An HLS playlist cannot contain both master and media tags.");
        }
        if (variants.Count > 0)
        {
            return new HlsPlaylistSnapshot(HlsPlaylistKind.Master, playlistUri, null, variants, []);
        }
        if (sawMediaTag)
        {
            return new HlsPlaylistSnapshot(HlsPlaylistKind.Media, playlistUri, mediaSequence, [], segments);
        }
        throw new InvalidDataException("The HLS playlist contains neither variants nor media segments.");
    }

    private static HlsVariantStream CreateVariant(Uri uri, IReadOnlyDictionary<string, string> attributes)
    {
        var bandwidth = ReadPositiveInt(attributes, "BANDWIDTH");
        int? width = null;
        int? height = null;
        if (attributes.TryGetValue("RESOLUTION", out var resolution))
        {
            var dimensions = resolution.Split('x', 2, StringSplitOptions.TrimEntries);
            if (dimensions.Length != 2
                || !int.TryParse(dimensions[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedWidth)
                || !int.TryParse(dimensions[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedHeight)
                || parsedWidth <= 0
                || parsedHeight <= 0)
            {
                throw new InvalidDataException("The HLS variant resolution is invalid.");
            }
            width = parsedWidth;
            height = parsedHeight;
        }
        attributes.TryGetValue("CODECS", out var codecs);
        return new HlsVariantStream(uri, bandwidth, width, height, codecs);
    }

    private static int? ReadPositiveInt(IReadOnlyDictionary<string, string> attributes, string name)
    {
        if (!attributes.TryGetValue(name, out var value))
        {
            return null;
        }
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new InvalidDataException($"The HLS {name.ToLowerInvariant()} value is invalid.");
        }
        return parsed;
    }

    private static Uri ResolveRequiredUri(
        IReadOnlyDictionary<string, string> attributes,
        string name,
        Uri playlistUri,
        string description)
    {
        if (!attributes.TryGetValue(name, out var value))
        {
            throw new InvalidDataException($"The HLS {description} has no {name} attribute.");
        }
        return ResolveUri(playlistUri, value, description);
    }

    private static Uri ResolveUri(Uri playlistUri, string value, string description)
    {
        try
        {
            return new Uri(playlistUri, value);
        }
        catch (UriFormatException exception)
        {
            throw new InvalidDataException($"The HLS {description} URI is invalid.", exception);
        }
    }

    private static string ValueAfterColon(string line)
    {
        var colon = line.IndexOf(':');
        return colon < 0 ? string.Empty : line[(colon + 1)..];
    }

    private static IReadOnlyDictionary<string, string> ParseAttributeList(string value)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        while (index < value.Length)
        {
            while (index < value.Length && (value[index] == ',' || char.IsWhiteSpace(value[index])))
            {
                index++;
            }
            if (index >= value.Length)
            {
                break;
            }

            var equals = value.IndexOf('=', index);
            if (equals < 0)
            {
                throw new InvalidDataException("The HLS attribute list is invalid.");
            }
            var name = value[index..equals].Trim();
            if (name.Length == 0)
            {
                throw new InvalidDataException("The HLS attribute name is empty.");
            }
            index = equals + 1;

            string attributeValue;
            if (index < value.Length && value[index] == '"')
            {
                var end = value.IndexOf('"', index + 1);
                if (end < 0)
                {
                    throw new InvalidDataException("The HLS quoted attribute is not terminated.");
                }
                attributeValue = value[(index + 1)..end];
                index = end + 1;
                if (index < value.Length && value[index] != ',')
                {
                    throw new InvalidDataException("The HLS attribute list is invalid.");
                }
            }
            else
            {
                var comma = value.IndexOf(',', index);
                if (comma < 0)
                {
                    attributeValue = value[index..].Trim();
                    index = value.Length;
                }
                else
                {
                    attributeValue = value[index..comma].Trim();
                    index = comma;
                }
            }

            if (attributeValue.Length == 0 || !attributes.TryAdd(name, attributeValue))
            {
                throw new InvalidDataException("The HLS attribute list contains an empty or duplicate value.");
            }
        }
        return attributes;
    }
}
