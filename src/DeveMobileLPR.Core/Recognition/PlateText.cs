using System.Text;

namespace DeveMobileLPR.Recognition;

public static class PlateText
{
    private static readonly IReadOnlyDictionary<string, (int First, int Second)> DutchPatterns =
        new Dictionary<string, (int First, int Second)>(StringComparer.Ordinal)
        {
            ["LLDDDD"] = (2, 2), ["DDDDLL"] = (2, 2), ["DDLLDD"] = (2, 2),
            ["LLDDLL"] = (2, 2), ["LLLLDD"] = (2, 2), ["DDLLLL"] = (2, 2),
            ["DDLLLD"] = (2, 3), ["DLLLDD"] = (1, 3), ["LLDDDL"] = (2, 3),
            ["LDDDLL"] = (1, 3), ["LLLDDL"] = (3, 2), ["LDDLLL"] = (1, 2),
            ["DLLDDD"] = (1, 2), ["DDDLLD"] = (3, 2)
        };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                result.Append(char.ToUpperInvariant(character));
            }
        }

        return result.ToString();
    }

    public static bool IsPlausibleDutchPlate(string? value)
    {
        var normalized = Normalize(value);
        if (normalized.Length != 6)
        {
            return false;
        }

        Span<char> pattern = stackalloc char[6];
        for (var index = 0; index < normalized.Length; index++)
        {
            pattern[index] = char.IsAsciiDigit(normalized[index]) ? 'D' : 'L';
        }

        return DutchPatterns.ContainsKey(pattern.ToString());
    }

    public static string FormatDutchPlate(string value)
    {
        var normalized = Normalize(value);
        if (normalized.Length != 6)
        {
            return normalized;
        }

        Span<char> pattern = stackalloc char[6];
        for (var index = 0; index < normalized.Length; index++)
        {
            pattern[index] = char.IsAsciiDigit(normalized[index]) ? 'D' : 'L';
        }

        if (!DutchPatterns.TryGetValue(pattern.ToString(), out var split))
        {
            return normalized;
        }

        var second = split.First + split.Second;
        return $"{normalized[..split.First]}-{normalized[split.First..second]}-{normalized[second..]}";
    }

    public static int EditDistance(string? left, string? right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        if (normalizedLeft.Length == 0)
        {
            return normalizedRight.Length;
        }

        if (normalizedRight.Length == 0)
        {
            return normalizedLeft.Length;
        }

        var previous = new int[normalizedRight.Length + 1];
        var current = new int[normalizedRight.Length + 1];
        for (var column = 0; column < previous.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= normalizedLeft.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= normalizedRight.Length; column++)
            {
                var substitution = previous[column - 1]
                    + (normalizedLeft[row - 1] == normalizedRight[column - 1] ? 0 : 1);
                current[column] = Math.Min(
                    Math.Min(previous[column] + 1, current[column - 1] + 1),
                    substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[normalizedRight.Length];
    }
}
