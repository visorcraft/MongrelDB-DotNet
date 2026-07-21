using System.Net;
using System.Text;
using System.Text.Json;
using Visorcraft.MongrelDB;
using Xunit;

namespace MongrelDB.Tests;

/// <summary>
/// Offline wire-shape conformance tests for <see cref="MongrelDBClient.CreateTableAsync"/>
/// and related admin endpoints.
///
/// These tests do NOT touch the network. They inject an <see cref="HttpMessageHandler"/>
/// stub that captures the outgoing request body and returns a canned 200 response,
/// then assert that the on-wire JSON keys and types survive the round-trip verbatim.
/// This guards against silent key renames or omissions that would break the wire
/// contract with the daemon.
/// </summary>
public class CreateTableWireShapeTests
{
    [Fact]
    public void QueryBuilderIncludesOffset()
    {
        using var client = new MongrelDBClient("http://127.0.0.1:1");
        Dictionary<string, object?> payload = client.Query("orders").Limit(10).Offset(12).Build();
        Assert.Equal(10L, payload["limit"]);
        Assert.Equal(12L, payload["offset"]);
    }

    // CapturingHandler subclasses HttpMessageHandler to intercept the outgoing
    // HttpRequestMessage. The real HTTP transport is never touched; the base URL
    // points at an unreachable port but no request is actually dispatched.
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }
        public string? CapturedBody { get; private set; }
        public Uri? CapturedUri { get; private set; }

        private string _responseBody = "{\"table_id\":7}";
        private string _responseContentType = "application/json";
        private HttpStatusCode _statusCode = HttpStatusCode.OK;

        public void SetResponse(string body, string contentType = "application/json")
        {
            _responseBody = body;
            _responseContentType = contentType;
            _statusCode = HttpStatusCode.OK;
        }

        public void SetErrorResponse(HttpStatusCode status, string body)
        {
            _statusCode = status;
            _responseBody = body;
            _responseContentType = "application/json";
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
                Content = new StringContent(_responseBody, Encoding.UTF8, _responseContentType),
            };
        }
    }

    private static string RunCreateTable(IList<Dictionary<string, object?>> columns, out CapturingHandler handler,
        IDictionary<string, object?>? constraints = null)
    {
        handler = new CapturingHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new MongrelDBClient(null, token: null, username: null, password: null, http);
        if (constraints is null)
            client.CreateTableAsync("wire_test", columns).GetAwaiter().GetResult();
        else
            client.CreateTableAsync("wire_test", columns, constraints).GetAwaiter().GetResult();
        return handler.CapturedBody ?? string.Empty;
    }

    [Fact]
    public void CreateTable_Posts_To_Kit_CreateTable_With_ApplicationJson()
    {
        RunCreateTable(new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1, ["name"] = "id", ["ty"] = "int64", ["primary_key"] = true, ["nullable"] = false },
        }, out CapturingHandler handler);

        Assert.NotNull(handler.CapturedRequest);
        Assert.Equal(HttpMethod.Post, handler.CapturedRequest!.Method);
        Assert.NotNull(handler.CapturedUri);
        Assert.Equal("/kit/create_table", handler.CapturedUri!.AbsolutePath);
        Assert.NotNull(handler.CapturedRequest.Content);
        Assert.Equal("application/json", handler.CapturedRequest.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void Static_Default_Matrix_Preserves_JSON_Scalars_In_One_Payload()
    {
        string body = RunCreateTable(new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1, ["name"] = "id", ["ty"] = "int64", ["primary_key"] = true, ["nullable"] = false },
            new() { ["id"] = 2, ["name"] = "string_default", ["ty"] = "varchar", ["nullable"] = false, ["default_value"] = "draft" },
            new() { ["id"] = 3, ["name"] = "number_default", ["ty"] = "int64", ["nullable"] = false, ["default_value"] = 7 },
            new() { ["id"] = 4, ["name"] = "bool_default", ["ty"] = "bool", ["nullable"] = false, ["default_value"] = true },
            new() { ["id"] = 5, ["name"] = "null_default", ["ty"] = "varchar", ["nullable"] = false, ["default_value"] = null },
            new() { ["id"] = 6, ["name"] = "now_literal", ["ty"] = "varchar", ["nullable"] = false, ["default_value"] = "now" },
            new() { ["id"] = 7, ["name"] = "now_expr", ["ty"] = "timestamp_nanos", ["nullable"] = false, ["default_expr"] = "now" },
            new() { ["id"] = 8, ["name"] = "uuid_literal", ["ty"] = "varchar", ["nullable"] = false, ["default_value"] = "uuid" },
            new() { ["id"] = 9, ["name"] = "uuid_expr", ["ty"] = "uuid", ["nullable"] = false, ["default_expr"] = "uuid" },
        }, out _);

        Assert.False(string.IsNullOrEmpty(body));
        using JsonDocument doc = JsonDocument.Parse(body);

        JsonElement Column(string name) => doc.RootElement.GetProperty("columns")
            .EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == name);

        // String literal: must round-trip as a JSON string, not an expression.
        JsonElement stringCol = Column("string_default");
        Assert.Equal(JsonValueKind.String, stringCol.GetProperty("default_value").ValueKind);
        Assert.Equal("draft", stringCol.GetProperty("default_value").GetString());

        // Number literal: must stay a JSON number.
        JsonElement numberCol = Column("number_default");
        Assert.Equal(JsonValueKind.Number, numberCol.GetProperty("default_value").ValueKind);
        Assert.Equal(7, numberCol.GetProperty("default_value").GetInt32());

        // Boolean literal: must stay a JSON true.
        JsonElement boolCol = Column("bool_default");
        Assert.Equal(JsonValueKind.True, boolCol.GetProperty("default_value").ValueKind);

        // Explicit null: must serialize as JSON null.
        JsonElement nullCol = Column("null_default");
        Assert.Equal(JsonValueKind.Null, nullCol.GetProperty("default_value").ValueKind);

        // Literal "now" in default_value is still just a string; it is NOT dynamic.
        JsonElement nowLiteralCol = Column("now_literal");
        Assert.Equal(JsonValueKind.String, nowLiteralCol.GetProperty("default_value").ValueKind);
        Assert.Equal("now", nowLiteralCol.GetProperty("default_value").GetString());

        // Dynamic default lives in default_expr and must not also emit default_value.
        JsonElement nowExprCol = Column("now_expr");
        Assert.False(nowExprCol.TryGetProperty("default_value", out _), "default_expr column must not also send default_value");
        Assert.Equal("now", nowExprCol.GetProperty("default_expr").GetString());

        // Literal "uuid" in default_value is still just a string; it is NOT dynamic.
        JsonElement uuidLiteralCol = Column("uuid_literal");
        Assert.Equal(JsonValueKind.String, uuidLiteralCol.GetProperty("default_value").ValueKind);
        Assert.Equal("uuid", uuidLiteralCol.GetProperty("default_value").GetString());

        // Dynamic uuid default lives in default_expr and must not also emit default_value.
        JsonElement uuidExprCol = Column("uuid_expr");
        Assert.False(uuidExprCol.TryGetProperty("default_value", out _), "default_expr column must not also send default_value");
        Assert.Equal("uuid", uuidExprCol.GetProperty("default_expr").GetString());
    }

    [Fact]
    public void CreateTable_Passes_EnumVariants_And_DefaultValue_Verbatim()
    {
        string body = RunCreateTable(new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1, ["name"] = "id", ["ty"] = "int64", ["primary_key"] = true, ["nullable"] = false },
            new()
            {
                ["id"] = 2,
                ["name"] = "status",
                ["ty"] = "enum",
                ["enum_variants"] = new List<object?> { "draft", "active", "archived" },
                ["default_value"] = "draft",
                ["default_expr"] = "uuid",
                ["nullable"] = false,
            },
        }, out _, new Dictionary<string, object?>
        {
            ["checks"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = 1,
                    ["name"] = "status_known",
                    ["expr"] = new Dictionary<string, object?>
                    {
                        ["Eq"] = new object[]
                        {
                            new Dictionary<string, object?> { ["Col"] = 2 },
                            new Dictionary<string, object?> { ["Lit"] = new Dictionary<string, object?> { ["Bytes"] = "draft" } },
                        },
                    },
                },
            },
        });

        Assert.False(string.IsNullOrEmpty(body));
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement statusCol = doc.RootElement.GetProperty("columns")
            .EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "status");

        // Keys must appear with the exact names the engine reads on the wire.
        Assert.True(statusCol.TryGetProperty("enum_variants", out _), "expected enum_variants key on status column");
        Assert.True(statusCol.TryGetProperty("default_value", out _), "expected default_value key on status column");
        Assert.Equal("uuid", statusCol.GetProperty("default_expr").GetString());
        Assert.Equal("status_known", doc.RootElement.GetProperty("constraints")
            .GetProperty("checks")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void CreateTable_Omits_EnumVariants_And_DefaultValue_When_Not_Provided()
    {
        string body = RunCreateTable(new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1, ["name"] = "id",   ["ty"] = "int64",   ["primary_key"] = true,  ["nullable"] = false },
            new() { ["id"] = 2, ["name"] = "name", ["ty"] = "varchar", ["primary_key"] = false, ["nullable"] = false },
        }, out _);

        Assert.False(string.IsNullOrEmpty(body));
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement nameCol = doc.RootElement.GetProperty("columns")
            .EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "name");

        // Regression: a column that doesn't set these keys must not serialize them
        // (no accidental `null` literals that the engine would later reject).
        Assert.False(nameCol.TryGetProperty("enum_variants", out _), "enum_variants must be absent when not set");
        Assert.False(nameCol.TryGetProperty("default_value", out _), "default_value must be absent when not set");
    }

    [Fact]
    public void AnnWithOptions_ReachWire()
    {
        // Build an ANN index against an embedding column using DiskANN as the
        // swappable backend. The on-wire JSON must carry the algorithm selector,
        // quantization mode, and the diskann-specific hyperparameters verbatim
        // so the daemon's ANN-backend dispatcher can read them.
        var diskannOptions = new Dictionary<string, object?>
        {
            ["r"] = 128L,
            ["l"] = 256L,
            ["beam_width"] = 8L,
            ["alpha"] = 1.2,
        };
        var annOptions = new Dictionary<string, object?>
        {
            ["algorithm"] = "diskann",
            ["quantization"] = "dense",
            ["diskann"] = diskannOptions,
        };
        var index = new Dictionary<string, object?>
        {
            ["name"] = "ann",
            ["column_id"] = 2L,
            ["kind"] = "ann",
            ["predicate"] = "embedding IS NOT NULL",
            ["options"] = new Dictionary<string, object?> { ["ann"] = annOptions },
        };
        var columns = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1L, ["name"] = "id", ["ty"] = "int64", ["primary_key"] = true, ["nullable"] = false },
            new() { ["id"] = 2L, ["name"] = "embedding", ["ty"] = "embedding(384)", ["nullable"] = false },
        };
        var indexes = new List<Dictionary<string, object?>> { index };

        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new MongrelDBClient(null, token: null, username: null, password: null, http);
        long tableId = client.CreateTableAsync("vectors", columns, null, indexes).GetAwaiter().GetResult();
        Assert.Equal(7L, tableId);

        string body = handler.CapturedBody ?? string.Empty;
        Assert.False(string.IsNullOrEmpty(body));

        // Parsed shape: the index reaches the wire as part of "indexes", with the
        // algorithm, quantization, and diskann sub-object all surviving intact.
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement wireIndex = doc.RootElement.GetProperty("indexes")
            .EnumerateArray()
            .First(i => i.GetProperty("kind").GetString() == "ann");
        Assert.Equal("embedding IS NOT NULL", wireIndex.GetProperty("predicate").GetString());

        JsonElement ann = wireIndex.GetProperty("options").GetProperty("ann");
        Assert.Equal("diskann", ann.GetProperty("algorithm").GetString());
        Assert.Equal("dense", ann.GetProperty("quantization").GetString());

        JsonElement diskann = ann.GetProperty("diskann");
        Assert.Equal(128L, diskann.GetProperty("r").GetInt64());
        Assert.Equal(256L, diskann.GetProperty("l").GetInt64());
        Assert.Equal(8L, diskann.GetProperty("beam_width").GetInt64());
        Assert.Equal(1.2, diskann.GetProperty("alpha").GetDouble());

        // Raw substring checks pin the exact on-wire markers the engine keys off,
        // including the diskann options block opening with its first hyperparameter.
        Assert.Contains("\"algorithm\":\"diskann\"", body);
        Assert.Contains("\"diskann\":{\"r\":128", body);
        Assert.Contains("\"quantization\":\"dense\"", body);
    }

    [Fact]
    public void SetHistoryRetentionEpochs_Puts_To_HistoryRetention_With_Exact_Body()
    {
        var handler = new CapturingHandler();
        handler.SetResponse("{\"history_retention_epochs\":100,\"earliest_retained_epoch\":1}");
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new MongrelDBClient(null, token: null, username: null, password: null, http);
        HistoryRetention retention = client.SetHistoryRetentionEpochsAsync(100).GetAwaiter().GetResult();

        Assert.Equal(100UL, retention.HistoryRetentionEpochs);
        Assert.Equal(1UL, retention.EarliestRetainedEpoch);
        Assert.NotNull(handler.CapturedRequest);
        Assert.Equal(HttpMethod.Put, handler.CapturedRequest!.Method);
        Assert.NotNull(handler.CapturedUri);
        Assert.Equal("/history/retention", handler.CapturedUri!.AbsolutePath);
        Assert.Equal("{\"history_retention_epochs\":100}", handler.CapturedBody);
    }

    [Fact]
    public void GetHistoryRetentionAsync_Uses_Get_Path_And_Decodes_Response_Fields()
    {
        var handler = new CapturingHandler();
        handler.SetResponse("{\"history_retention_epochs\":100,\"earliest_retained_epoch\":42}");
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new MongrelDBClient(null, token: null, username: null, password: null, http);

        HistoryRetention retention = client.GetHistoryRetentionAsync().GetAwaiter().GetResult();

        Assert.Equal(100UL, retention.HistoryRetentionEpochs);
        Assert.Equal(42UL, retention.EarliestRetainedEpoch);
        Assert.NotNull(handler.CapturedRequest);
        Assert.Equal(HttpMethod.Get, handler.CapturedRequest!.Method);
        Assert.Equal("/history/retention", handler.CapturedUri!.AbsolutePath);
    }

    [Fact]
    public void HistoryRetentionEpochsAsync_Returns_Single_Field()
    {
        var handler = new CapturingHandler();
        handler.SetResponse("{\"history_retention_epochs\":100,\"earliest_retained_epoch\":42}");
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new MongrelDBClient(null, token: null, username: null, password: null, http);

        ulong epochs = client.HistoryRetentionEpochsAsync().GetAwaiter().GetResult();
        Assert.Equal(100UL, epochs);
        Assert.Equal(HttpMethod.Get, handler.CapturedRequest!.Method);
        Assert.Equal("/history/retention", handler.CapturedUri!.AbsolutePath);
    }

    [Fact]
    public void EarliestRetainedEpochAsync_Returns_Single_Field()
    {
        var handler = new CapturingHandler();
        handler.SetResponse("{\"history_retention_epochs\":100,\"earliest_retained_epoch\":42}");
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new MongrelDBClient(null, token: null, username: null, password: null, http);

        ulong earliest = client.EarliestRetainedEpochAsync().GetAwaiter().GetResult();
        Assert.Equal(42UL, earliest);
        Assert.Equal(HttpMethod.Get, handler.CapturedRequest!.Method);
        Assert.Equal("/history/retention", handler.CapturedUri!.AbsolutePath);
    }

    [Fact]
    public void SetHistoryRetentionEpochsAsync_Propagates_Non2xx_Response()
    {
        var handler = new CapturingHandler();
        handler.SetErrorResponse(HttpStatusCode.ServiceUnavailable,
            "{\"error\":{\"message\":\"unavailable\",\"code\":\"STORAGE_ERROR\"}}");
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new MongrelDBClient(null, token: null, username: null, password: null, http);

        QueryException ex = Assert.Throws<QueryException>(() =>
            client.SetHistoryRetentionEpochsAsync(100).GetAwaiter().GetResult());
        Assert.Equal(503, ex.Status);
    }

    [Fact]
    public void GetHistoryRetentionAsync_Propagates_Non2xx_Response()
    {
        var handler = new CapturingHandler();
        handler.SetErrorResponse(HttpStatusCode.ServiceUnavailable,
            "{\"error\":{\"message\":\"unavailable\",\"code\":\"STORAGE_ERROR\"}}");
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new MongrelDBClient(null, token: null, username: null, password: null, http);

        QueryException ex = Assert.Throws<QueryException>(() =>
            client.GetHistoryRetentionAsync().GetAwaiter().GetResult());
        Assert.Equal(503, ex.Status);
    }
}
