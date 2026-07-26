using System.Net;
using System.Text;
using System.Text.Json;
using DeveMobileLPR.RdwDownloader;

namespace DeveMobileLPR.Tests;

public sealed class SocrataRdwClientTests
{
    [Fact]
    public async Task GetSnapshotAndCountAsync_ParseOfficialResponseShapesAndSendAppToken()
    {
        var handler = new RecordingHandler((request, _) => request.Method == HttpMethod.Get
            ? Json("""
                {"id":"m9d7-ebf2","name":"Gekentekende voertuigen","rowsUpdatedAt":1720000000,
                 "columns":[{"fieldName":"kenteken"},{"fieldName":"merk"}]}
                """)
            : Json("[{\"count\":\"16827986\"}]"));
        using var http = Client(handler);
        var source = new SocrataRdwClient(http, "test-token");

        var snapshot = await source.GetSnapshotAsync(RdwDatasets.Vehicles, CancellationToken.None);
        var count = await source.GetRowCountAsync(RdwDatasets.Vehicles, CancellationToken.None);

        Assert.Equal("Gekentekende voertuigen", snapshot.Name);
        Assert.Equal(1_720_000_000L, snapshot.RowsUpdatedAt);
        Assert.Contains("kenteken", snapshot.Fields);
        Assert.Equal(16_827_986L, count);
        Assert.All(handler.Requests, request => Assert.Equal("test-token", request.AppToken));
    }

    [Fact]
    public async Task GetVehiclePageAsync_UsesKeysetQueryAndParsesQuotedNumericValues()
    {
        const string csv = "kenteken,merk,handelsbenaming,catalogusprijs,datum_eerste_toelating,inrichting\r\n" +
                           "AB12CD,AUDI,A6,\"85,250\",20240219,personenauto\r\n" +
                           "XY99ZZ,,,,,\r\n";
        var handler = new RecordingHandler((_, _) => Csv(csv));
        using var http = Client(handler);
        var source = new SocrataRdwClient(http, null);

        var rows = await source.GetVehiclePageAsync("AA00AA", 2, CancellationToken.None);
        var request = Assert.Single(handler.Requests);

        Assert.Equal(2, rows.Count);
        Assert.Equal(85_250, rows[0].CatalogPrice);
        Assert.Equal(2024, rows[0].RegistrationYear);
        Assert.Null(rows[1].CatalogPrice);
        Assert.Contains("WHERE kenteken > 'AA00AA'", request.Query, StringComparison.Ordinal);
        Assert.Contains("ORDER BY kenteken LIMIT 2", request.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFuelPageAsync_UsesCompositeCursor()
    {
        const string csv = "kenteken,brandstof_volgnummer,brandstof_omschrijving\nAB12CD,2,Elektriciteit\n";
        var handler = new RecordingHandler((_, _) => Csv(csv));
        using var http = Client(handler);
        var source = new SocrataRdwClient(http, null);

        var row = Assert.Single(await source.GetFuelPageAsync("AB12CD", "1", 50, CancellationToken.None));
        var query = Assert.Single(handler.Requests).Query;

        Assert.Equal("Elektriciteit", row.Description);
        Assert.Contains("kenteken > 'AB12CD'", query, StringComparison.Ordinal);
        Assert.Contains("brandstof_volgnummer > '1'", query, StringComparison.Ordinal);
        Assert.Contains("ORDER BY kenteken,brandstof_volgnummer LIMIT 50", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRowCountAsync_RetriesTransientResponses()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var handler = new RecordingHandler((_, _) => ++attempts == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : Json("[{\"count\":\"10\"}]"));
        using var http = Client(handler);
        var source = new SocrataRdwClient(http, null, delay: (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        Assert.Equal(10L, await source.GetRowCountAsync(RdwDatasets.Vehicles, CancellationToken.None));
        Assert.Equal(2, attempts);
        Assert.Single(delays);
    }

    [Theory]
    [InlineData("AB12CD", null)]
    [InlineData(null, "1")]
    public async Task GetFuelPageAsync_RejectsHalfCursor(string? plate, string? sequence)
    {
        using var http = Client(new RecordingHandler((_, _) => Csv(string.Empty)));
        var source = new SocrataRdwClient(http, null);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            source.GetFuelPageAsync(plate, sequence, 1, CancellationToken.None));
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://opendata.rdw.nl/"),
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Csv(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "text/csv")
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? query = null;
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                query = document.RootElement.TryGetProperty("query", out var element)
                    ? element.GetString()
                    : null;
            }

            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                query,
                request.Headers.TryGetValues("X-App-Token", out var values) ? values.Single() : null));
            return responseFactory(request, cancellationToken);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? Query, string? AppToken);
}
