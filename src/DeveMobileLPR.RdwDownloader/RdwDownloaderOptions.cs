using System.Globalization;

namespace DeveMobileLPR.RdwDownloader;

internal sealed record RdwDownloaderOptions(
    string OutputPath,
    string? AppToken,
    int PageSize,
    long? SampleRows,
    bool Restart,
    bool ShowHelp)
{
    public const int DefaultPageSize = 50_000;
    public const string AppTokenEnvironmentVariable = "SOCRATA_APP_TOKEN";

    public static RdwDownloaderOptions Parse(
        IReadOnlyList<string> arguments,
        string currentDirectory,
        string? environmentAppToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        var output = Path.Combine(currentDirectory, "artifacts", "rdw", "rdw.sqlite");
        var token = string.IsNullOrWhiteSpace(environmentAppToken) ? null : environmentAppToken.Trim();
        var pageSize = DefaultPageSize;
        long? sampleRows = null;
        var restart = false;
        var showHelp = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--output":
                case "-o":
                    output = RequireValue(arguments, ref index, argument);
                    break;
                case "--app-token":
                    token = RequireValue(arguments, ref index, argument);
                    break;
                case "--page-size":
                    pageSize = ParseInt32(RequireValue(arguments, ref index, argument), argument, 1, 50_000);
                    break;
                case "--sample-rows":
                    sampleRows = ParseInt64(RequireValue(arguments, ref index, argument), argument, 1, long.MaxValue);
                    break;
                case "--restart":
                    restart = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{argument}'. Use --help for usage.");
            }
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            throw new ArgumentException("The output path cannot be empty.");
        }

        return new RdwDownloaderOptions(
            Path.GetFullPath(output, currentDirectory),
            string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
            pageSize,
            sampleRows,
            restart,
            showHelp);
    }

    public static string Usage =>
        """
        Build the offline SQLite RDW database consumed by DeveMobileLPR.

        Usage:
          dotnet run --project src/DeveMobileLPR.RdwDownloader -- [options]

        Options:
          -o, --output <path>       Final SQLite path.
                                    Default: artifacts/rdw/rdw.sqlite
          --page-size <1..50000>    Rows committed per resumable API page.
                                    Default: 50000
          --sample-rows <count>     Build a clearly marked bounded test database.
          --app-token <token>       Optional Socrata application token. Prefer the
                                    SOCRATA_APP_TOKEN environment variable.
          --restart                 Delete the partial .building database and restart.
          -h, --help                Show this help.

        An interrupted run resumes from <output>.building. The existing final output
        remains untouched until the replacement has downloaded and validated fully.
        """;

    private static string RequireValue(IReadOnlyList<string> arguments, ref int index, string option)
    {
        if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
        {
            throw new ArgumentException($"Option '{option}' requires a value.");
        }

        return arguments[index];
    }

    private static int ParseInt32(string value, string option, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ||
            result < minimum || result > maximum)
        {
            throw new ArgumentException($"Option '{option}' must be between {minimum} and {maximum}.");
        }

        return result;
    }

    private static long ParseInt64(string value, string option, long minimum, long maximum)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ||
            result < minimum || result > maximum)
        {
            throw new ArgumentException($"Option '{option}' must be between {minimum} and {maximum}.");
        }

        return result;
    }
}
