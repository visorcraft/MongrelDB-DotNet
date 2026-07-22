using System.Net;
using System.Text;
using System.Text.Json;
using Visorcraft.MongrelDB;
using Xunit;

namespace MongrelDB.Tests;

/// <summary>
/// Offline unit tests for 0.64 durable HLC recovery parsers, retrieve_text
/// request shape, and multi-retriever <see cref="SearchBuilder"/> payload assembly.
/// No live daemon required.
/// </summary>
public class DurableRetrieveTests
{
    [Fact]
    public void QueryStatusParsesStructuralHlcWithoutStringParsing()
    {
        var hlc = new Dictionary<string, object?>
        {
            ["physical_micros"] = 1_700_000_000_000_000L,
            ["logical"] = 3,
            ["node_tiebreaker"] = 7,
        };
        var outcome = new Dictionary<string, object?>
        {
            ["committed"] = true,
            ["committed_statements"] = 1,
            ["last_commit_epoch"] = 17L,
            ["last_commit_epoch_text"] = "17",
            ["last_commit_hlc"] = hlc,
            ["first_commit_statement_index"] = 0,
            ["last_commit_statement_index"] = 0,
            ["completed_statements"] = 1,
            ["statement_index"] = 0,
            ["serialization"] = "succeeded",
            ["serialization_state"] = "succeeded",
        };
        var fixture = new Dictionary<string, object?>
        {
            ["query_id"] = "abcdefabcdefabcdefabcdefabcdefab",
            ["status"] = "committed",
            ["state"] = "completed",
            ["server_state"] = "completed",
            ["terminal_state"] = "committed",
            ["committed"] = true,
            ["last_commit_epoch"] = 17L,
            ["last_commit_hlc"] = hlc,
            ["outcome"] = outcome,
            ["durable"] = outcome,
        };

        QueryStatus status = QueryStatus.FromMap(fixture);
        Assert.True(status.Committed);
        CommitHlc? parsed = status.GetCommitHlc();
        Assert.NotNull(parsed);
        Assert.Equal(1_700_000_000_000_000UL, parsed!.PhysicalMicros);
        Assert.Equal(3U, parsed.Logical);
        Assert.Equal(7U, parsed.NodeTiebreaker);
        Assert.Equal("succeeded", status.GetSerializationState());
        Assert.Equal(17UL, status.Outcome.LastCommitEpoch);
    }

    [Fact]
    public void QueryStatusParseJsonStructuralHlc()
    {
        // Fixture mirrors mongreldb-server GET /queries/{id} (0.64+).
        const string raw = """
            {
              "query_id": "abcdefabcdefabcdefabcdefabcdefab",
              "status": "committed",
              "state": "completed",
              "server_state": "completed",
              "terminal_state": "committed",
              "operation": "INSERT",
              "committed": true,
              "committed_statements": 1,
              "last_commit_epoch": 17,
              "last_commit_epoch_text": "17",
              "last_commit_hlc": {
                "physical_micros": 1700000000000000,
                "logical": 3,
                "node_tiebreaker": 7
              },
              "first_commit_statement_index": 0,
              "last_commit_statement_index": 0,
              "completed_statements": 1,
              "statement_index": 0,
              "cancel_outcome": null,
              "cancellation_reason": "none",
              "retryable": false,
              "outcome": {
                "committed": true,
                "committed_statements": 1,
                "last_commit_epoch": 17,
                "last_commit_epoch_text": "17",
                "last_commit_hlc": {
                  "physical_micros": 1700000000000000,
                  "logical": 3,
                  "node_tiebreaker": 7
                },
                "first_commit_statement_index": 0,
                "last_commit_statement_index": 0,
                "completed_statements": 1,
                "statement_index": 0,
                "serialization": "succeeded",
                "serialization_state": "succeeded",
                "terminal_state": "committed"
              },
              "durable": {
                "committed": true,
                "committed_statements": 1,
                "last_commit_epoch": 17,
                "last_commit_epoch_text": "17",
                "last_commit_hlc": {
                  "physical_micros": 1700000000000000,
                  "logical": 3,
                  "node_tiebreaker": 7
                },
                "first_commit_statement_index": 0,
                "last_commit_statement_index": 0,
                "completed_statements": 1,
                "statement_index": 0,
                "serialization": "succeeded",
                "serialization_state": "succeeded",
                "terminal_state": "committed"
              },
              "terminal_error": null
            }
            """;
        QueryStatus status = QueryStatus.ParseJson(Encoding.UTF8.GetBytes(raw));
        Assert.True(status.Committed);
        CommitHlc? hlc = status.GetCommitHlc();
        Assert.NotNull(hlc);
        Assert.Equal(1_700_000_000_000_000UL, hlc!.PhysicalMicros);
        Assert.Equal(3U, hlc.Logical);
        Assert.Equal(7U, hlc.NodeTiebreaker);
        Assert.Equal("succeeded", status.GetSerializationState());
        Assert.Equal(17UL, status.Outcome.LastCommitEpoch);
    }

    [Fact]
    public void MultiRetrieverSearchBuildIncludesTwoRetrieversAndFusion()
    {
        using var client = new MongrelDBClient("http://127.0.0.1:9");
        Dictionary<string, object?> payload = client.Search("docs")
            .AnnRetriever("ann", 3L, new[] { 0.1, 0.2 }, k: 10, weight: 1.0)
            .SparseRetriever("sparse", 4L, new (long, double)[] { (1, 0.5), (2, 0.25) }, k: 10, weight: 0.5)
            .Fusion(60)
            .Limit(5)
            .Build();

        Assert.True(payload.ContainsKey("retrievers"));
        Assert.True(payload.ContainsKey("fusion"));
        var retrievers = Assert.IsAssignableFrom<System.Collections.IList>(payload["retrievers"]);
        Assert.Equal(2, retrievers.Count);
        Assert.Equal(5L, payload["limit"]);
    }

    [Fact]
    public void RetrieveTextRequestShape()
    {
        Dictionary<string, object?> payload = RetrieveText.BuildRequest(
            "docs",
            3,
            "cat sat",
            new RetrieveTextOptions { K = 5 });

        Assert.Equal("docs", payload["table"]);
        Assert.Equal(3, payload["embedding_column"]);
        Assert.Equal("cat sat", payload["text"]);
        Assert.Equal(5, payload["k"]);

        // Round-trip through the same JSON encoder the HTTP client uses.
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        using JsonDocument doc = JsonDocument.Parse(bytes);
        Assert.Equal("docs", doc.RootElement.GetProperty("table").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("embedding_column").GetInt32());
        Assert.Equal("cat sat", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("k").GetInt32());
    }

    [Fact]
    public void CommitHlcFromMapRejectsMissingPhysical()
    {
        Assert.Null(CommitHlc.FromMap(new Dictionary<string, object?> { ["logical"] = 1 }));
        Assert.Null(CommitHlc.FromMap(null));
        Assert.Null(CommitHlc.FromMap("not-a-map"));
    }

    [Fact]
    public async Task QueryStatusAndCancelQueryHitExpectedPaths()
    {
        var handler = new CapturingHandler();
        handler.SetResponse("""
            {
              "query_id": "qid1",
              "status": "running",
              "state": "running",
              "committed": false,
              "outcome": { "serialization_state": "in_progress" },
              "last_commit_hlc": null
            }
            """);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var client = new MongrelDBClient(null, token: null, username: null, password: null, http);

        QueryStatus status = await client.QueryStatusAsync("qid1");
        Assert.Equal("qid1", status.QueryId);
        Assert.NotNull(handler.CapturedUri);
        Assert.EndsWith("/queries/qid1", handler.CapturedUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.CapturedRequest!.Method);

        handler.SetResponse("""{"cancel_outcome":"requested"}""");
        Dictionary<string, object?> cancel = await client.CancelQueryAsync("qid1");
        Assert.Equal("requested", cancel["cancel_outcome"]?.ToString());
        Assert.EndsWith("/queries/qid1/cancel", handler.CapturedUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.CapturedRequest!.Method);
    }

    [Fact]
    public async Task RetrieveTextPostsToKitRetrieveText()
    {
        var handler = new CapturingHandler();
        handler.SetResponse("""{"hits":[],"provenance":{}}""");
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var client = new MongrelDBClient(null, token: null, username: null, password: null, http);

        Dictionary<string, object?> result = await client.RetrieveTextAsync(
            "docs", 3, "hello", new RetrieveTextOptions { K = 8 });
        Assert.NotNull(result);
        Assert.NotNull(handler.CapturedUri);
        Assert.EndsWith("/kit/retrieve_text", handler.CapturedUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.CapturedRequest!.Method);
        Assert.Contains("\"table\":\"docs\"", handler.CapturedBody);
        Assert.Contains("\"embedding_column\":3", handler.CapturedBody);
        Assert.Contains("\"text\":\"hello\"", handler.CapturedBody);
        Assert.Contains("\"k\":8", handler.CapturedBody);
    }

    // Local capturing handler (mirrors CreateTableWireShapeTests).
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }
        public string? CapturedBody { get; private set; }
        public Uri? CapturedUri { get; private set; }

        private string _responseBody = "{}";
        private HttpStatusCode _statusCode = HttpStatusCode.OK;

        public void SetResponse(string body)
        {
            _responseBody = body;
            _statusCode = HttpStatusCode.OK;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            CapturedUri = request.RequestUri;
            if (request.Content is not null)
            {
                byte[] bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                CapturedBody = Encoding.UTF8.GetString(bytes);
            }
            else
            {
                CapturedBody = null;
            }
            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
