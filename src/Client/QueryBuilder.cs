using System.Text.Json;

namespace Visorcraft.MongrelDB;

/// <summary>
/// Builds a request for the daemon's <c>/kit/query</c> endpoint, where
/// conditions push down to the engine's specialized indexes for sub-millisecond
/// lookups.
/// </summary>
/// <remarks>
/// <para>
/// Condition parameters accept friendly aliases that are translated to the
/// server's exact on-wire keys before sending (see <see cref="Where"/>):
/// </para>
/// <list type="bullet">
///   <item><c>column</c>         → <c>column_id</c></item>
///   <item><c>min</c> / <c>max</c> → <c>lo</c> / <c>hi</c></item>
///   <item><c>min_inclusive</c>  → <c>lo_inclusive</c></item>
///   <item><c>max_inclusive</c>  → <c>hi_inclusive</c></item>
/// </list>
/// <para>
/// The server's canonical keys are accepted directly too.
/// </para>
/// <para>
/// Usage:
/// </para>
/// <code>
/// var rows = await db.Query("orders")
///     .Where("range", new Dictionary&lt;string, object?&gt; { ["column"] = 3L, ["min"] = 100.0, ["max"] = 150.0 })
///     .Projection(new long[] { 1, 2 })
///     .Limit(100)
///     .ExecuteAsync();
/// if (builder.Truncated)
/// {
///     // result set hit the limit; more matches exist on the server
/// }
/// </code>
/// </remarks>
public sealed class QueryBuilder
{
    private readonly MongrelDBClient _client;
    private readonly string _table;
    private readonly List<Dictionary<string, object?>> _conditions = new();
    private long[]? _projection;
    private long? _limit;
    private bool _lastTruncated;

    internal QueryBuilder(MongrelDBClient client, string table)
    {
        _client = client;
        _table = table;
    }

    /// <summary>
    /// Adds a native condition. Conditions are AND-ed together.
    /// </summary>
    /// <remarks>
    /// Available condition types include:
    /// <list type="bullet">
    ///   <item><c>pk</c> - exact primary-key match (<c>{value: pk}</c>)</item>
    ///   <item><c>bitmap_eq</c> - equality on a bitmap-indexed column</item>
    ///   <item><c>bitmap_in</c> - IN predicate on a bitmap-indexed column</item>
    ///   <item><c>range</c> - integer range predicate (lo/hi, inclusive)</item>
    ///   <item><c>range_f64</c> - float range predicate (lo/hi + lo_inclusive/hi_inclusive)</item>
    ///   <item><c>is_null</c> - null check</item>
    ///   <item><c>is_not_null</c> - non-null check</item>
    ///   <item><c>fm_contains</c> - full-text substring search (FM-index)</item>
    ///   <item><c>fm_contains_all</c> - multiple substring patterns (all must match)</item>
    ///   <item><c>ann</c> - dense vector similarity search (HNSW)</item>
    ///   <item><c>sparse_match</c> - sparse vector match</item>
    ///   <item><c>min_hash_similar</c> - MinHash similarity search</item>
    /// </list>
    /// </remarks>
    /// <param name="condType">The condition type.</param>
    /// <param name="params">The condition parameters (friendly aliases accepted).</param>
    /// <returns>This builder, for chaining.</returns>
    public QueryBuilder Where(string condType, IDictionary<string, object?> @params)
    {
        ArgumentNullException.ThrowIfNull(condType);
        ArgumentNullException.ThrowIfNull(@params);
        var entry = new Dictionary<string, object?>
        {
            [condType] = NormalizeCondition(condType, @params),
        };
        _conditions.Add(entry);
        return this;
    }

    /// <summary>
    /// Sets the column ids to return. A null projection (the default) means all
    /// columns.
    /// </summary>
    /// <param name="columnIDs">The projection, or null for all columns.</param>
    /// <returns>This builder, for chaining.</returns>
    public QueryBuilder Projection(long[]? columnIDs)
    {
        _projection = columnIDs == null ? null : (long[])columnIDs.Clone();
        return this;
    }

    /// <summary>
    /// Caps the number of rows returned.
    /// </summary>
    /// <param name="limit">The row limit.</param>
    /// <returns>This builder, for chaining.</returns>
    public QueryBuilder Limit(long limit)
    {
        _limit = limit;
        return this;
    }

    /// <summary>
    /// Builds the request payload that will be sent to <c>/kit/query</c>.
    /// </summary>
    /// <returns>The request payload.</returns>
    public Dictionary<string, object?> Build()
    {
        var payload = new Dictionary<string, object?>
        {
            ["table"] = _table,
        };
        if (_conditions.Count > 0)
        {
            // The daemon expects externally-tagged conditions: [{type: {...}}, ...]
            payload["conditions"] = _conditions;
        }
        if (_projection is not null)
        {
            payload["projection"] = _projection;
        }
        if (_limit is not null)
        {
            payload["limit"] = _limit.Value;
        }
        return payload;
    }

    /// <summary>
    /// Runs the query and returns the matching rows. Also records whether the
    /// result was truncated by <see cref="Limit"/>; check it with
    /// <see cref="Truncated"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The matching rows (never null).</returns>
    public async Task<List<Dictionary<string, object?>>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        byte[] body = await _client.PostAsync("/kit/query", Build(), cancellationToken).ConfigureAwait(false);

        var rows = new List<Dictionary<string, object?>>();
        bool truncated = false;
        if (body.Length > 0)
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("rows", out JsonElement rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement row in rowsEl.EnumerateArray())
                    {
                        rows.Add(row.ValueKind == JsonValueKind.Object ? ReadObject(row) : new Dictionary<string, object?>());
                    }
                }
                if (doc.RootElement.TryGetProperty("truncated", out JsonElement truncEl))
                {
                    truncated = truncEl.ValueKind == JsonValueKind.True
                        || (truncEl.ValueKind == JsonValueKind.String && bool.TryParse(truncEl.GetString(), out bool parsed) && parsed);
                }
            }
        }
        _lastTruncated = truncated;
        return rows;
    }

    /// <summary>
    /// Reports whether the most recent <see cref="ExecuteAsync"/> result was
    /// capped by the query limit. Returns <see langword="false"/> until
    /// <see cref="ExecuteAsync"/> has been called.
    /// </summary>
    public bool Truncated => _lastTruncated;

    /// <summary>
    /// Translates friendly parameter aliases to the server's canonical on-wire
    /// keys. Both spellings are accepted, so callers may use whichever is
    /// clearer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Generic aliases (applied to all condition types):
    /// </para>
    /// <list type="bullet">
    ///   <item><c>column</c>        → <c>column_id</c></item>
    ///   <item><c>min</c>           → <c>lo</c></item>
    ///   <item><c>max</c>           → <c>hi</c></item>
    ///   <item><c>min_inclusive</c> → <c>lo_inclusive</c></item>
    ///   <item><c>max_inclusive</c> → <c>hi_inclusive</c></item>
    /// </list>
    /// <para>
    /// Type-specific aliases:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>fm_contains</c> / <c>fm_contains_all</c>: <c>value</c> → <c>pattern</c>
    ///       (other types like <c>pk</c>/<c>bitmap_eq</c> use <c>value</c> as
    ///       their canonical key, so the <c>value</c>→<c>pattern</c> alias must
    ///       NOT apply globally)</item>
    /// </list>
    /// </remarks>
    private static Dictionary<string, object?> NormalizeCondition(string condType, IDictionary<string, object?> @params)
    {
        var normalized = new Dictionary<string, object?>(@params.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> entry in @params)
        {
            string key = entry.Key;
            string canonical = key switch
            {
                "column" => "column_id",
                "min" => "lo",
                "max" => "hi",
                "min_inclusive" => "lo_inclusive",
                "max_inclusive" => "hi_inclusive",
                "value" => (condType == "fm_contains" || condType == "fm_contains_all") ? "pattern" : "value",
                _ => key,
            };
            normalized[canonical] = entry.Value;
        }
        return normalized;
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
}
