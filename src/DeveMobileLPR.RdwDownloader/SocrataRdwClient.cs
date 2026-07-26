using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Sylvan.Data.Csv;

namespace DeveMobileLPR.RdwDownloader;

internal sealed class SocrataRdwClient : IRdwSource
{
    private const int MaximumAttempts = 6;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);
    private readonly HttpClient _httpClient;
    private readonly string? _appToken;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string>? _diagnostic;

    public SocrataRdwClient(
        HttpClient httpClient,
        string? appToken,
        Action<string>? diagnostic = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _appToken = string.IsNullOrWhiteSpace(appToken) ? null : appToken.Trim();
        _diagnostic = diagnostic;
        _delay = delay ?? Task.Delay;
    }

    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://opendata.rdw.nl/", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeveMobileLPR-RdwDownloader/1.0");
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        return client;
    }

    public Task<DatasetSnapshot> GetSnapshotAsync(string datasetId, CancellationToken cancellationToken) =>
        ExecuteWithRetryAsync(async attemptToken =>
        {
            using var request = CreateRequest(HttpMethod.Get, $"api/views/{datasetId}");
            using var response = await SendAsync(request, attemptToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(attemptToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: attemptToken).ConfigureAwait(false);
            var root = document.RootElement;
            var fields = root.GetProperty("columns")
                .EnumerateArray()
                .Select(column => column.GetProperty("fieldName").GetString())
                .Where(field => !string.IsNullOrWhiteSpace(field))
                .Select(field => field!)
                .ToHashSet(StringComparer.Ordinal);
            return new DatasetSnapshot(
                root.GetProperty("id").GetString() ?? datasetId,
                root.GetProperty("name").GetString() ?? datasetId,
                root.GetProperty("rowsUpdatedAt").GetInt64(),
                fields);
        }, cancellationToken);

    public Task<long> GetRowCountAsync(string datasetId, CancellationToken cancellationToken) =>
        ExecuteWithRetryAsync(async attemptToken =>
        {
            using var request = CreateJsonRequest(
                $"api/v3/views/{datasetId}/query.json",
                new { query = "SELECT count(*) AS count", includeSystem = false, includeSynthetic = false });
            using var response = await SendAsync(request, attemptToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(attemptToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: attemptToken).ConfigureAwait(false);
            var count = document.RootElement[0].GetProperty("count").GetString();
            return long.Parse(count ?? throw new InvalidDataException("RDW count response omitted 'count'."), CultureInfo.InvariantCulture);
        }, cancellationToken);

    public Task<IReadOnlyList<VehicleSourceRow>> GetVehiclePageAsync(
        string? afterPlate,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        var where = string.IsNullOrEmpty(afterPlate)
            ? string.Empty
            : $" WHERE kenteken > '{EscapeSoqlLiteral(afterPlate)}'";
        var query =
            "SELECT kenteken,merk,handelsbenaming,catalogusprijs,datum_eerste_toelating,inrichting" +
            where +
            $" ORDER BY kenteken LIMIT {limit.ToString(CultureInfo.InvariantCulture)}";

        return ReadCsvPageAsync(query, 6, csv => new VehicleSourceRow(
            Required(csv, 0, "kenteken"),
            Optional(csv, 1),
            Optional(csv, 2),
            ParseCatalogPrice(Optional(csv, 3)),
            ParseRegistrationYear(Optional(csv, 4)),
            Optional(csv, 5)), cancellationToken);
    }

    public Task<IReadOnlyList<FuelSourceRow>> GetFuelPageAsync(
        string? afterPlate,
        string? afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        if ((afterPlate is null) != (afterSequence is null))
        {
            throw new ArgumentException("Fuel cursors must either both be set or both be null.");
        }

        var where = afterPlate is null
            ? string.Empty
            : $" WHERE kenteken > '{EscapeSoqlLiteral(afterPlate)}' OR " +
              $"(kenteken = '{EscapeSoqlLiteral(afterPlate)}' AND brandstof_volgnummer > '{EscapeSoqlLiteral(afterSequence!)}')";
        var query =
            "SELECT kenteken,brandstof_volgnummer,brandstof_omschrijving" +
            where +
            $" ORDER BY kenteken,brandstof_volgnummer LIMIT {limit.ToString(CultureInfo.InvariantCulture)}";

        return ReadCsvPageAsync(query, 3, csv => new FuelSourceRow(
            Required(csv, 0, "kenteken"),
            Required(csv, 1, "brandstof_volgnummer"),
            Optional(csv, 2)), cancellationToken);
    }

    private Task<IReadOnlyList<T>> ReadCsvPageAsync<T>(
        string query,
        int expectedColumns,
        Func<CsvDataReader, T> map,
        CancellationToken cancellationToken) =>
        ExecuteWithRetryAsync<IReadOnlyList<T>>(async attemptToken =>
        {
            using var request = CreateJsonRequest(
                $"api/v3/views/{DatasetId<T>()}/export.csv",
                new
                {
                    query,
                    timeout = 600,
                    serializationOptions = new { separator = ",", bom = false }
                });
            using var response = await SendAsync(request, attemptToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(attemptToken).ConfigureAwait(false);
            using var textReader = new StreamReader(stream, Encoding.UTF8, true, 128 * 1024, leaveOpen: false);
            using var csv = await CsvDataReader.CreateAsync(
                textReader,
                new CsvDataReaderOptions { HasHeaders = true }).ConfigureAwait(false);
            if (csv.FieldCount != expectedColumns)
            {
                throw new InvalidDataException($"RDW CSV returned {csv.FieldCount} columns; expected {expectedColumns}.");
            }

            var rows = new List<T>();
            while (await csv.ReadAsync(attemptToken).ConfigureAwait(false))
            {
                rows.Add(map(csv));
            }

            return rows;
        }, cancellationToken);

    private static string DatasetId<T>() =>
        typeof(T) == typeof(VehicleSourceRow) ? RdwDatasets.Vehicles : RdwDatasets.Fuels;

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativeUri)
    {
        var request = new HttpRequestMessage(method, relativeUri);
        if (_appToken is not null)
        {
            request.Headers.TryAddWithoutValidation("X-App-Token", _appToken);
        }

        return request;
    }

    private HttpRequestMessage CreateJsonRequest(string relativeUri, object body)
    {
        var request = CreateRequest(HttpMethod.Post, relativeUri);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (IsTransient(response.StatusCode))
        {
            var retryAfter = response.Headers.RetryAfter?.Delta;
            var statusCode = response.StatusCode;
            response.Dispose();
            throw new TransientRdwException($"RDW returned HTTP {(int)statusCode} ({statusCode}).", retryAfter);
        }

        response.EnsureSuccessStatusCode();
        return response;
    }

    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                return await operation(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException($"RDW request exceeded {RequestTimeout.TotalMinutes:F0} minutes.", exception);
            }
            catch (Exception exception) when (IsRetryable(exception))
            {
                lastException = exception;
            }

            if (attempt == MaximumAttempts)
            {
                break;
            }

            var delay = lastException is TransientRdwException { RetryAfter: { } retryAfter }
                ? retryAfter
                : TimeSpan.FromSeconds(Math.Pow(2, attempt));
            _diagnostic?.Invoke($"RDW request failed ({lastException!.Message}). Retrying in {delay.TotalSeconds:F0}s ({attempt}/{MaximumAttempts}).");
            await _delay(delay, cancellationToken).ConfigureAwait(false);
        }

        throw new HttpRequestException(
            $"RDW request failed after {MaximumAttempts} attempts.",
            lastException);
    }

    private static bool IsRetryable(Exception exception) =>
        exception is HttpRequestException or IOException or TimeoutException or TransientRdwException;

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static string Required(CsvDataReader csv, int ordinal, string fieldName)
    {
        var value = Optional(csv, ordinal);
        return value ?? throw new InvalidDataException($"RDW row contains no {fieldName}.");
    }

    private static string? Optional(CsvDataReader csv, int ordinal)
    {
        if (csv.IsDBNull(ordinal))
        {
            return null;
        }

        var value = csv.GetString(ordinal).Trim();
        return value.Length == 0 ? null : value;
    }

    internal static long? ParseCatalogPrice(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) ||
            price < 0 || price > long.MaxValue || price != decimal.Truncate(price))
        {
            throw new InvalidDataException($"RDW catalog price '{value}' is not a non-negative whole number.");
        }

        return decimal.ToInt64(price);
    }

    internal static int? ParseRegistrationYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length < 4 ||
            !int.TryParse(trimmed.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
            year is < 1800 or > 2200)
        {
            throw new InvalidDataException($"RDW first-registration date '{value}' has no valid year.");
        }

        return year;
    }

    internal static string EscapeSoqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 50_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "RDW page size must be between 1 and 50000.");
        }
    }

    private sealed class TransientRdwException(string message, TimeSpan? retryAfter) : IOException(message)
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}
