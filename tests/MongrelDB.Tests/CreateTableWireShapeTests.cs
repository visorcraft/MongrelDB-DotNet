using System.Net;
using System.Text;
using System.Text.Json;
using Visorcraft.MongrelDB;
using Xunit;

namespace MongrelDB.Tests;

/// <summary>
/// Offline wire-shape conformance tests for <see cref="MongrelDBClient.CreateTableAsync"/>.
///
/// These tests do NOT touch the network. They inject an <see cref="HttpMessageHandler"/>
/// stub that captures the outgoing request body and returns a canned 200 response,
/// then assert that the <c>enum_variants</c> and <c>default_value</c> column keys
/// survive the JSON round-trip verbatim. This guards against silent key renames or
/// omissions that would break the wire contract with the daemon (the engine reads
/// both keys directly out of the column hash).
/// </summary>
public class CreateTableWireShapeTests
{
    [Fact]
    public void Static_Default_Matrix_Preserves_JSON_Scalars()
    {
        object?[] values = ["text", 3, true, null, "now"];
        foreach (object? value in values)
        {
            string json = JsonSerializer.Serialize(new Dictionary<string, object?> { ["default_value"] = value });
            using JsonDocument doc = JsonDocument.Parse(json);
            Assert.Equal(JsonSerializer.Serialize(value), doc.RootElement.GetProperty("default_value").GetRawText());
        }
    }
    // CapturingHandler subclasses HttpMessageHandler to intercept the outgoing
    // HttpRequestMessage. The real HTTP transport is never touched; the base URL
    // points at an unreachable port but no request is actually dispatched.
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }
        public string? CapturedBody { get; private set; }
        public Uri? CapturedUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            CapturedUri = request.RequestUri;
            if (request.Content is not null)
            {
                byte[] bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                CapturedBody = Encoding.UTF8.GetString(bytes);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"table_id\":7}", Encoding.UTF8, "application/json"),
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
}
