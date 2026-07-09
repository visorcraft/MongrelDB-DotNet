using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Visorcraft.MongrelDB;

/// <summary>
/// The MongrelDB HTTP client. A pure managed .NET client for a running
/// <c>mongreldb-server</c> daemon, built on <see cref="HttpClient"/> (.NET 8+).
/// No external dependencies. The API mirrors the MongrelDB PHP, Go, and Java
/// clients: typed CRUD, a fluent query builder that pushes conditions down to
/// the engine's native indexes, idempotent batch transactions, full SQL access,
/// and schema introspection.
/// </summary>
/// <remarks>
/// <para>
/// Connect with a base URL:
/// </para>
/// <code>
/// using var db = new MongrelDBClient("http://127.0.0.1:8453");
/// bool ok = await db.HealthAsync();
/// </code>
/// <para>
/// A <see cref="MongrelDBClient"/> instance is safe for concurrent use by
/// multiple threads once constructed: the underlying <see cref="HttpClient"/>
/// is thread-safe and the instance is immutable after configuration. The
/// client accepts an externally-owned <see cref="HttpClient"/> via the
/// constructor overload; when owned, it is disposed by <see cref="Dispose"/>.
/// </para>
/// <para>
/// See <see href="https://www.MongrelDB.com">MongrelDB</see>.
/// </para>
/// </remarks>
public sealed class MongrelDBClient : IDisposable
{
    /// <summary>
    /// The daemon address used when none is supplied.
    /// </summary>
    public const string DefaultBaseURL = "http://127.0.0.1:8453";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // The daemon's JSON is ASCII-friendly, but we disable escaping and relax
    // number handling so that integer values that exceed Int64 round-trip as
    // decimals without throwing. Property names use the server's exact keys,
    // so case-insensitive matching keeps decoding tolerant.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _baseURL;
    private readonly string? _token;
    private readonly string? _username;
    private readonly string? _password;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>
    /// Constructs a client for the daemon at <paramref name="baseUrl"/> with no
    /// authentication. A null/empty URL falls back to <see cref="DefaultBaseURL"/>.
    /// </summary>
    /// <param name="baseUrl">The daemon base URL (e.g. <c>http://127.0.0.1:8453</c>).</param>
    public MongrelDBClient(string? baseUrl)
        : this(baseUrl, token: null, username: null, password: null, http: null)
    {
    }

    /// <summary>
    /// Constructs a client for the daemon at <paramref name="baseUrl"/> with
    /// optional authentication.
    /// </summary>
    /// <remarks>
    /// A non-null/non-empty <paramref name="token"/> authenticates requests
    /// with a Bearer header (<c>--auth-token</c> mode) and takes precedence
    /// over basic-auth credentials. When <paramref name="token"/> is null, a
    /// non-null/non-empty <paramref name="username"/> enables HTTP Basic auth
    /// (<c>--auth-users</c> mode); the password may be null.
    /// </remarks>
    /// <param name="baseUrl">The daemon base URL, or null for the default.</param>
    /// <param name="token">A Bearer token, or null.</param>
    /// <param name="username">The Basic-auth username, or null.</param>
    /// <param name="password">The Basic-auth password, or null.</param>
    public MongrelDBClient(string? baseUrl, string? token, string? username, string? password)
        : this(baseUrl, token, username, password, http: null)
    {
    }

    /// <summary>
    /// Constructs a client with full control over the underlying transport.
    /// </summary>
    /// <param name="baseUrl">The daemon base URL, or null for the default.</param>
    /// <param name="token">A Bearer token, or null.</param>
    /// <param name="username">The Basic-auth username, or null.</param>
    /// <param name="password">The Basic-auth password, or null.</param>
    /// <param name="http">
    /// A custom <see cref="HttpClient"/>, or null to create one with a
    /// 30-second timeout. When non-null, the caller retains ownership and is
    /// responsible for disposing it; this client will not dispose it.
    /// </param>
    public MongrelDBClient(string? baseUrl, string? token, string? username, string? password, HttpClient? http)
    {
        string base_ = string.IsNullOrEmpty(baseUrl) ? DefaultBaseURL : baseUrl!;
        base_ = base_.TrimEnd('/');
        _baseURL = base_;
        _token = string.IsNullOrEmpty(token) ? null : token;
        _username = string.IsNullOrEmpty(username) ? null : username;
        _password = password ?? string.Empty;

        if (http is not null)
        {
            _http = http;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient { Timeout = DefaultTimeout };
            _ownsHttp = true;
        }
    }

    /// <summary>
    /// The daemon base URL this client was configured with.
    /// </summary>
    public string BaseURL => _baseURL;

    // ── Health & tables ───────────────────────────────────────────────────

    /// <summary>
    /// Reports whether the daemon is reachable and healthy.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> (rather than throwing) when the daemon
    /// is unreachable or returns a non-2xx status.
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns><see langword="true"/> if the daemon answered <c>/health</c> with a 2xx.</returns>
    public async Task<bool> HealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await GetAsync("/health", cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (MongrelDBException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Lists all table names in the database.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>A list of table names (never null).</returns>
    public async Task<List<string>> GetTableNamesAsync(CancellationToken cancellationToken = default)
    {
        byte[] body = await GetAsync("/tables", cancellationToken).ConfigureAwait(false);
        if (body.Length == 0)
        {
            return new List<string>();
        }

        using JsonDocument doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new QueryException($"mongreldb: unexpected table-list response: {Preview(body)}");
        }

        var names = new List<string>();
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            names.Add(el.ValueKind == JsonValueKind.Null ? null! : el.ToString());
        }
        return names;
    }

    /// <summary>
    /// Creates a table named <paramref name="name"/> with the given columns and
    /// returns the assigned table id.
    /// </summary>
    /// <remarks>
    /// Each column is a <see cref="Dictionary{TKey, TValue}"/> sent verbatim to
    /// the daemon. Recognized keys are <c>id</c>, <c>name</c>, <c>ty</c>,
    /// <c>primary_key</c>, and <c>nullable</c>.
    /// </remarks>
    /// <param name="name">The table name.</param>
    /// <param name="columns">The column descriptors.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The assigned table id.</returns>
    public async Task<long> CreateTableAsync(string name, IList<Dictionary<string, object?>> columns, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(columns);

        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["columns"] = columns,
        };
        byte[] body = await PostAsync("/kit/create_table", payload, cancellationToken).ConfigureAwait(false);

        var resp = Deserialize<CreateTableResponse>(body);
        return resp?.TableID ?? 0L;
    }

    /// <summary>
    /// Drops a table by name.
    /// </summary>
    /// <param name="name">The table name.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    public async Task DropTableAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        await DeletePathAsync("/tables/" + UrlPathEscape(name), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the row count for a table.
    /// </summary>
    /// <param name="table">The table name.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The number of rows.</returns>
    public async Task<long> CountAsync(string table, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        byte[] body = await GetAsync("/tables/" + UrlPathEscape(table) + "/count", cancellationToken).ConfigureAwait(false);
        var resp = Deserialize<CountResponse>(body);
        if (resp == null || resp.Count == null)
        {
            throw new QueryException("mongreldb: malformed count response");
        }
        return resp.Count.Value;
    }

    // ── CRUD (via the Kit typed transaction endpoint) ─────────────────────

    /// <summary>
    /// Inserts a row. <paramref name="idempotencyKey"/>, when non-null and
    /// non-empty, makes the commit safe to retry - the daemon returns the
    /// original result on duplicate commits.
    /// </summary>
    /// <param name="table">The target table.</param>
    /// <param name="cells">A column-id-to-value map (flattened to the server's <c>[col_id, value, ...]</c> array before sending).</param>
    /// <param name="idempotencyKey">An idempotency key, or null.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The per-operation result object (the first element of the server's results array), or an empty map if none.</returns>
    public async Task<Dictionary<string, object?>> PutAsync(string table, Cells cells, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(cells);

        var op = new Dictionary<string, object?>
        {
            ["put"] = new Dictionary<string, object?>
            {
                ["table"] = table,
                ["cells"] = FlattenCells(cells),
            },
        };
        List<Dictionary<string, object?>> results = await CommitOneAsync(new[] { op }, idempotencyKey, cancellationToken).ConfigureAwait(false);
        return FirstResult(results);
    }

    /// <summary>
    /// Inserts a row, or updates it on a primary-key conflict.
    /// <paramref name="updateCells"/>, when non-null, supplies the values
    /// written on conflict; null means DO NOTHING.
    /// </summary>
    /// <param name="table">The target table.</param>
    /// <param name="cells">The column-id-to-value map to insert.</param>
    /// <param name="updateCells">The values written on conflict, or null.</param>
    /// <param name="idempotencyKey">An idempotency key, or null.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The per-operation result object, or an empty map if none.</returns>
    public async Task<Dictionary<string, object?>> UpsertAsync(string table, Cells cells, Cells? updateCells = null, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(cells);

        var upsert = new Dictionary<string, object?>
        {
            ["table"] = table,
            ["cells"] = FlattenCells(cells),
        };
        if (updateCells is not null)
        {
            upsert["update_cells"] = FlattenCells(updateCells);
        }
        var op = new Dictionary<string, object?> { ["upsert"] = upsert };
        List<Dictionary<string, object?>> results = await CommitOneAsync(new[] { op }, idempotencyKey, cancellationToken).ConfigureAwait(false);
        return FirstResult(results);
    }

    /// <summary>
    /// Removes a row by its internal row id.
    /// </summary>
    /// <param name="table">The target table.</param>
    /// <param name="rowId">The internal row id.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    public async Task DeleteAsync(string table, long rowId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        var op = new Dictionary<string, object?>
        {
            ["delete"] = new Dictionary<string, object?>
            {
                ["table"] = table,
                ["row_id"] = rowId,
            },
        };
        await CommitOneAsync(new[] { op }, idempotencyKey: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a row by its primary-key value.
    /// </summary>
    /// <param name="table">The target table.</param>
    /// <param name="pk">The primary-key value.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    public async Task DeleteByPkAsync(string table, object? pk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(pk);
        var op = new Dictionary<string, object?>
        {
            ["delete_by_pk"] = new Dictionary<string, object?>
            {
                ["table"] = table,
                ["pk"] = pk,
            },
        };
        await CommitOneAsync(new[] { op }, idempotencyKey: null, cancellationToken).ConfigureAwait(false);
    }

    // commitOneAsync sends a single-op transaction and returns the results array.
    private async Task<List<Dictionary<string, object?>>> CommitOneAsync(IList<Dictionary<string, object?>> ops, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?> { ["ops"] = ops };
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            payload["idempotency_key"] = idempotencyKey;
        }
        byte[] body = await PostAsync("/kit/txn", payload, cancellationToken).ConfigureAwait(false);
        return DecodeResults(body);
    }

    // ── Query ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a fluent <see cref="QueryBuilder"/> against <paramref name="table"/>.
    /// </summary>
    /// <param name="table">The table to query.</param>
    /// <returns>A new query builder.</returns>
    public QueryBuilder Query(string table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new QueryBuilder(this, table);
    }

    // ── SQL ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes a SQL statement via the <c>/sql</c> endpoint.
    /// </summary>
    /// <remarks>
    /// When the daemon returns a JSON result set, the rows are decoded and
    /// returned; for statements that yield no rows (DDL/DML) or a non-JSON
    /// (Arrow IPC) body, it returns an empty list and a null (absent) error.
    /// </remarks>
    /// <param name="sql">The SQL statement.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The decoded rows, or an empty list.</returns>
    public async Task<List<Dictionary<string, object?>>> SqlAsync(string sql, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var payload = new Dictionary<string, object?> { ["sql"] = sql };
        byte[] body = await PostAsync("/sql", payload, cancellationToken).ConfigureAwait(false);

        byte first = FirstNonWhitespace(body);
        if (first == (byte)'\0')
        {
            return new List<Dictionary<string, object?>>();
        }

        // The /sql endpoint generally streams Arrow IPC bytes for SELECTs; only
        // decode when the body is actually JSON to avoid noise.
        if (first == (byte)'{' || first == (byte)'[')
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return ReadRowList(doc.RootElement);
            }
            // A single JSON object (e.g. an error envelope) is not a row set.
        }
        return new List<Dictionary<string, object?>>();
    }

    // ── Schema ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the full schema catalog: a table-name-to-descriptor map.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The schema catalog (never null).</returns>
    public async Task<Dictionary<string, Dictionary<string, object?>>> GetSchemaAsync(CancellationToken cancellationToken = default)
    {
        byte[] body = await GetAsync("/kit/schema", cancellationToken).ConfigureAwait(false);
        var @out = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        if (body.Length == 0)
        {
            return @out;
        }

        using JsonDocument doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return @out;
        }
        if (doc.RootElement.TryGetProperty("tables", out JsonElement tables) && tables.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in tables.EnumerateObject())
            {
                @out[prop.Name] = ReadObject(prop.Value);
            }
        }
        return @out;
    }

    /// <summary>
    /// Returns the descriptor for a single table.
    /// </summary>
    /// <param name="table">The table name.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The table descriptor (never null).</returns>
    public async Task<Dictionary<string, object?>> GetSchemaForAsync(string table, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        byte[] body = await GetAsync("/kit/schema/" + UrlPathEscape(table), cancellationToken).ConfigureAwait(false);
        if (body.Length == 0)
        {
            return new Dictionary<string, object?>();
        }
        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            ? ReadObject(doc.RootElement)
            : new Dictionary<string, object?>();
    }

    // ── Maintenance ───────────────────────────────────────────────────────

    /// <summary>
    /// Merges sorted runs across all tables.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The daemon's response object.</returns>
    public async Task<Dictionary<string, object?>> CompactAsync(CancellationToken cancellationToken = default)
        => await PostDecodeAsync("/compact", cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Merges sorted runs for a single table.
    /// </summary>
    /// <param name="table">The table to compact.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The daemon's response object.</returns>
    public async Task<Dictionary<string, object?>> CompactTableAsync(string table, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        return await PostDecodeAsync("/tables/" + UrlPathEscape(table) + "/compact", cancellationToken).ConfigureAwait(false);
    }

    // postDecodeAsync POSTs an empty body and decodes the JSON object response.
    private async Task<Dictionary<string, object?>> PostDecodeAsync(string path, CancellationToken cancellationToken)
    {
        byte[] body = await PostAsync(path, body: null, cancellationToken).ConfigureAwait(false);
        if (body.Length == 0)
        {
            return new Dictionary<string, object?>();
        }
        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            ? ReadObject(doc.RootElement)
            : new Dictionary<string, object?>();
    }

    // ── Transactions ──────────────────────────────────────────────────────

    /// <summary>
    /// Starts a new batch transaction. Operations staged on the returned
    /// <see cref="Transaction"/> are committed atomically in a single
    /// <c>/kit/txn</c> request.
    /// </summary>
    /// <returns>A new transaction.</returns>
    public Transaction BeginTransaction() => new(this);

    /// <summary>
    /// Sends a batch of staged operations atomically. Exposed for the
    /// <see cref="Transaction"/> type; returns the per-operation results array.
    /// </summary>
    /// <param name="ops">The operations to commit.</param>
    /// <param name="idempotencyKey">An idempotency key, or null.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The per-operation results, or an empty list if <paramref name="ops"/> is empty.</returns>
    internal async Task<List<Dictionary<string, object?>>> CommitTxnAsync(IList<Dictionary<string, object?>> ops, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (ops.Count == 0)
        {
            return new List<Dictionary<string, object?>>();
        }
        var payload = new Dictionary<string, object?> { ["ops"] = ops };
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            payload["idempotency_key"] = idempotencyKey;
        }
        byte[] body = await PostAsync("/kit/txn", payload, cancellationToken).ConfigureAwait(false);
        return DecodeResults(body);
    }

    // ── HTTP plumbing ─────────────────────────────────────────────────────

    internal Task<byte[]> GetAsync(string path, CancellationToken cancellationToken)
        => DoRequestAsync(HttpMethod.Get, path, body: null, cancellationToken);

    internal Task<byte[]> PostAsync(string path, object? body, CancellationToken cancellationToken)
        => DoRequestAsync(HttpMethod.Post, path, body, cancellationToken);

    internal Task<byte[]> DeletePathAsync(string path, CancellationToken cancellationToken)
        => DoRequestAsync(HttpMethod.Delete, path, body: null, cancellationToken);

    /// <summary>
    /// Builds and runs one request. The server's JSON extractors require an
    /// explicit Content-Type header on any request carrying a JSON body, so one
    /// is added whenever the body is non-null. Non-2xx responses are mapped to
    /// typed client exceptions via <see cref="ToException"/>.
    /// </summary>
    private async Task<byte[]> DoRequestAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        // The server's extractor requires the route to be relative to the
        // configured base URL. We concatenate manually to avoid
        // System.Uri's treatment of '+' and '%'.
        string url = _baseURL + "/" + path.TrimStart('/');

        using var req = new HttpRequestMessage(method, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpContent? content = null;
        if (body is not null)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
            content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Content = content;
        }
        ApplyAuth(req);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new QueryException($"mongreldb: request {method} {path} failed: {ex.Message}", ex);
        }

        using (resp)
        using (content)
        {
            byte[] data = resp.Content is null ? Array.Empty<byte>() : await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            int status = (int)resp.StatusCode;
            if (status < 200 || status >= 300)
            {
                throw ToException(status, data);
            }
            return data;
        }
    }

    /// <summary>
    /// Sets the Authorization header according to the configured credentials.
    /// A bearer token takes precedence over basic auth.
    /// </summary>
    private void ApplyAuth(HttpRequestMessage req)
    {
        if (_token is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }
        else if (_username is not null)
        {
            string creds = _username + ":" + _password;
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(creds));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Flattens a column-id-to-value map to the server's flat
    /// <c>[col_id, value, col_id, value, ...]</c> array. Pair order is not
    /// significant - each value is preceded by its own column id.
    /// </summary>
    internal static List<object?> FlattenCells(Cells cells)
    {
        var flat = new List<object?>(cells.Count * 2);
        foreach (KeyValuePair<long, object?> entry in cells)
        {
            flat.Add(entry.Key);
            flat.Add(entry.Value);
        }
        return flat;
    }

    // decodeResults pulls the results array out of a /kit/txn response.
    private static List<Dictionary<string, object?>> DecodeResults(byte[] body)
    {
        if (FirstNonWhitespace(body) == (byte)'\0')
        {
            return new List<Dictionary<string, object?>>();
        }
        using JsonDocument doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new QueryException("mongreldb: decode txn response: unexpected JSON");
        }
        var @out = new List<Dictionary<string, object?>>();
        if (doc.RootElement.TryGetProperty("results", out JsonElement results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement r in results.EnumerateArray())
            {
                @out.Add(r.ValueKind == JsonValueKind.Object ? ReadObject(r) : new Dictionary<string, object?>());
            }
        }
        return @out;
    }

    // firstResult returns the first element of results, or an empty map.
    private static Dictionary<string, object?> FirstResult(List<Dictionary<string, object?>> results)
        => results.Count == 0 ? new Dictionary<string, object?>() : results[0];

    private static byte FirstNonWhitespace(byte[] body)
    {
        for (int i = 0; i < body.Length; i++)
        {
            byte b = body[i];
            if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\n' && b != (byte)'\r')
            {
                return b;
            }
        }
        return (byte)'\0';
    }

    private static string Preview(byte[] body)
    {
        string s = Encoding.UTF8.GetString(body);
        return s.Length > 120 ? s.Substring(0, 120) + "..." : s;
    }

    private static T? Deserialize<T>(byte[] body)
    {
        if (body.Length == 0)
        {
            return default;
        }
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    private static Dictionary<string, object?> ReadObject(JsonElement el)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (JsonProperty prop in el.EnumerateObject())
        {
            dict[prop.Name] = ReadValue(prop.Value);
        }
        return dict;
    }

    private static List<Dictionary<string, object?>> ReadRowList(JsonElement el)
    {
        var list = new List<Dictionary<string, object?>>();
        if (el.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        foreach (JsonElement row in el.EnumerateArray())
        {
            list.Add(row.ValueKind == JsonValueKind.Object ? ReadObject(row) : new Dictionary<string, object?>());
        }
        return list;
    }

    private static object? ReadValue(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Object => ReadObject(el),
            JsonValueKind.Array => ReadArray(el),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => ReadNumber(el),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => el.ToString(),
        };
    }

    private static List<object?> ReadArray(JsonElement el)
    {
        var list = new List<object?>();
        foreach (JsonElement item in el.EnumerateArray())
        {
            list.Add(ReadValue(item));
        }
        return list;
    }

    private static object ReadNumber(JsonElement el)
    {
        // Preserve integer precision: try exact integer types first (Int64,
        // UInt64), then decimal for large fixed-point, then double for
        // floating-point. Trying double before UInt64/decimal loses precision
        // for big integers.
        if (el.TryGetInt64(out long l))
        {
            return l;
        }
        if (el.TryGetUInt64(out ulong u))
        {
            return u;
        }
        if (decimal.TryParse(el.GetRawText(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal dec))
        {
            return dec;
        }
        if (el.TryGetDouble(out double d))
        {
            return d;
        }
        return el.GetRawText();
    }

    /// <summary>
    /// Percent-encodes a path segment (used for table names that may contain
    /// characters unsafe in a URL). Leaves the forward slash intact so compound
    /// identifiers survive.
    /// </summary>
    internal static string UrlPathEscape(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return string.Empty;
        }
        var sb = new StringBuilder(segment.Length);
        foreach (char c in segment)
        {
            // Only RFC 3986 unreserved characters pass through unescaped.
            // '/' is encoded so a table name cannot inject an extra path segment.
            if (c == '-' || c == '_' || c == '.' || c == '~'
                || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                sb.Append(c);
            }
            else
            {
                // Encode each UTF-8 byte of the char.
                foreach (byte b in Encoding.UTF8.GetBytes(c.ToString()))
                {
                    sb.Append('%');
                    sb.Append(HexChar((byte)(b >> 4)));
                    sb.Append(HexChar((byte)(b & 0x0F)));
                }
            }
        }
        return sb.ToString();
    }

    private static char HexChar(int n) => (char)(n < 10 ? '0' + n : 'A' + (n - 10));

    // Maps an HTTP status code and response body to a typed exception. It
    // best-effort decodes the server's JSON error envelope
    // ({error:{message,code,op_index}}) and falls back to the raw body.
    private static MongrelDBException ToException(int status, byte[] body)
    {
        string? message = null;
        string? code = null;
        int? opIndex = null;

        if (FirstNonWhitespace(body) == (byte)'{')
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    JsonElement root = doc.RootElement;
                    // Prefer the nested {"error": {...}} envelope.
                    if (root.TryGetProperty("error", out JsonElement err) && err.ValueKind == JsonValueKind.Object)
                    {
                        message = StrOrNull(err, "message");
                        code = StrOrNull(err, "code");
                        opIndex = ReadInt(err, "op_index");
                    }
                    // Fall back to a flat {"message": ..., "code": ...} object.
                    if (message is null && code is null && opIndex is null)
                    {
                        message = StrOrNull(root, "message");
                        code = StrOrNull(root, "code");
                    }
                }
            }
            catch (JsonException)
            {
                // Fall through to the raw-body fallback below.
            }
        }

        if (message is null && body.Length > 0)
        {
            message = Encoding.UTF8.GetString(body);
        }

        if (string.IsNullOrEmpty(message))
        {
            message = status switch
            {
                401 or 403 => $"authentication failed ({status})",
                404 => "resource not found",
                409 => "constraint violation",
                _ => $"server error ({status})",
            };
        }

        return status switch
        {
            401 or 403 => new AuthException(message!, status, code, opIndex),
            404 => new NotFoundException(message!, status, code, opIndex),
            409 => new ConflictException(message!, status, code, opIndex),
            _ => new QueryException(message!, status, code, opIndex),
        };
    }

    private static string? StrOrNull(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int? ReadInt(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement el) && el.TryGetInt32(out int v) ? v : null;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    // Wire response shapes for the typed decoders above.
    private sealed class CreateTableResponse
    {
        [JsonPropertyName("table_id")]
        public long TableID { get; set; }
    }

    private sealed class CountResponse
    {
        [JsonPropertyName("count")]
        public long? Count { get; set; }
    }
}

/// <summary>
/// A column-id-to-value map. The client flattens it to the server's on-wire
/// <c>[col_id, value, col_id, value, ...]</c> array before sending. Pair order
/// is irrelevant - each value is preceded by its own column id.
/// </summary>
public sealed class Cells : Dictionary<long, object?>
{
    /// <summary>Initializes a new empty <see cref="Cells"/> map.</summary>
    public Cells() : base() { }

    /// <summary>Initializes a new <see cref="Cells"/> map with the given capacity.</summary>
    public Cells(int capacity) : base(capacity) { }

    /// <summary>Creates a <see cref="Cells"/> map from alternating column-id/value pairs.</summary>
    /// <param name="kv">Alternating <c>columnId, value, columnId, value, ...</c> entries.</param>
    /// <returns>A populated <see cref="Cells"/> map.</returns>
    public static Cells Of(params object?[] kv)
    {
        var cells = new Cells(kv.Length / 2);
        for (int i = 0; i + 1 < kv.Length; i += 2)
        {
            long id = kv[i] switch
            {
                long l => l,
                int i32 => i32,
                _ => Convert.ToInt64(kv[i], System.Globalization.CultureInfo.InvariantCulture),
            };
            cells[id] = kv[i + 1];
        }
        return cells;
    }
}
