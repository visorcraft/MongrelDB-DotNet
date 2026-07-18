using System.Text.Json;

namespace Visorcraft.MongrelDB;

/// <summary>
/// Builds a request for the daemon's <c>POST /kit/search</c> endpoint:
/// multi-retriever hybrid search with reciprocal-rank fusion and optional
/// exact-vector rerank. Wire format matches KitSearchRequest (flattened retrievers).
/// </summary>
public sealed class SearchBuilder
{
    private readonly MongrelDBClient _client;
    private readonly string _table;
    private readonly List<Dictionary<string, object?>> _must = new();
    private readonly List<Dictionary<string, object?>> _retrievers = new();
    private Dictionary<string, object?> _fusion = new()
    {
        ["reciprocal_rank"] = new Dictionary<string, object?> { ["constant"] = 60 },
    };
    private Dictionary<string, object?>? _rerank;
    private long _limit = 10;
    private long[]? _projection;
    private bool _explain;
    private string? _cursor;

    internal SearchBuilder(MongrelDBClient client, string table)
    {
        _client = client;
        _table = table;
    }

    /// <summary>Hard filter (same condition shapes as <see cref="QueryBuilder.Where"/>).</summary>
    public SearchBuilder Must(string condType, IDictionary<string, object?> @params)
    {
        ArgumentNullException.ThrowIfNull(condType);
        _must.Add(new Dictionary<string, object?>
        {
            [condType] = QueryBuilder.NormalizeConditionPublic(condType, @params),
        });
        return this;
    }

    public SearchBuilder AnnRetriever(string name, long columnId, IReadOnlyList<double> query, long k = 64, double weight = 1.0)
    {
        _retrievers.Add(new Dictionary<string, object?>
        {
            ["name"] = name,
            ["weight"] = weight,
            ["ann"] = new Dictionary<string, object?>
            {
                ["column_id"] = columnId,
                ["query"] = query,
                ["k"] = k,
            },
        });
        return this;
    }

    /// <summary><paramref name="terms"/> is a list of (tokenId, weight) pairs.</summary>
    public SearchBuilder SparseRetriever(string name, long columnId, IReadOnlyList<(long token, double weight)> terms, long k = 64, double weight = 1.0)
    {
        var pairs = new List<object?[]>();
        foreach (var (token, w) in terms)
        {
            pairs.Add(new object?[] { token, w });
        }
        _retrievers.Add(new Dictionary<string, object?>
        {
            ["name"] = name,
            ["weight"] = weight,
            ["sparse"] = new Dictionary<string, object?>
            {
                ["column_id"] = columnId,
                ["query"] = pairs,
                ["k"] = k,
            },
        });
        return this;
    }

    public SearchBuilder MinHashRetriever(string name, long columnId, IReadOnlyList<string> members, long k = 64, double weight = 1.0)
    {
        _retrievers.Add(new Dictionary<string, object?>
        {
            ["name"] = name,
            ["weight"] = weight,
            ["min_hash"] = new Dictionary<string, object?>
            {
                ["column_id"] = columnId,
                ["members"] = members,
                ["k"] = k,
            },
        });
        return this;
    }

    public SearchBuilder Fusion(uint constant = 60)
    {
        if (constant == 0)
        {
            constant = 60;
        }
        _fusion = new Dictionary<string, object?>
        {
            ["reciprocal_rank"] = new Dictionary<string, object?> { ["constant"] = constant },
        };
        return this;
    }

    /// <summary><paramref name="metric"/> is cosine, dot_product, or euclidean.</summary>
    public SearchBuilder ExactRerank(long embeddingColumn, IReadOnlyList<double> query, string metric = "cosine", long candidateLimit = 64, double weight = 1.0)
    {
        _rerank = new Dictionary<string, object?>
        {
            ["exact_vector"] = new Dictionary<string, object?>
            {
                ["embedding_column"] = embeddingColumn,
                ["query"] = query,
                ["metric"] = metric,
                ["candidate_limit"] = candidateLimit,
                ["weight"] = weight,
            },
        };
        return this;
    }

    public SearchBuilder Limit(long limit)
    {
        _limit = limit;
        return this;
    }

    public SearchBuilder Projection(long[]? columnIDs)
    {
        _projection = columnIDs;
        return this;
    }

    public SearchBuilder Explain(bool on = true)
    {
        _explain = on;
        return this;
    }

    public SearchBuilder Cursor(string? cursor)
    {
        _cursor = cursor;
        return this;
    }

    public Dictionary<string, object?> Build()
    {
        if (_retrievers.Count == 0)
        {
            throw new ArgumentException("search requires at least one retriever");
        }
        if (_limit <= 0)
        {
            throw new ArgumentException("search limit must be positive");
        }
        var payload = new Dictionary<string, object?>
        {
            ["table"] = _table,
            ["retrievers"] = _retrievers,
            ["fusion"] = _fusion,
            ["limit"] = _limit,
        };
        if (_must.Count > 0)
        {
            payload["must"] = _must;
        }
        if (_rerank is not null)
        {
            payload["rerank"] = _rerank;
        }
        if (_projection is not null)
        {
            payload["projection"] = _projection;
        }
        if (_explain)
        {
            payload["explain"] = true;
        }
        if (!string.IsNullOrEmpty(_cursor))
        {
            payload["cursor"] = _cursor;
        }
        return payload;
    }

    /// <summary>Executes the hybrid search and returns the decoded JSON object.</summary>
    public async Task<Dictionary<string, object?>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        byte[] body = await _client.PostAsync("/kit/search", Build(), cancellationToken).ConfigureAwait(false);
        if (body.Length == 0)
        {
            return new Dictionary<string, object?> { ["hits"] = new List<object?>() };
        }
        using JsonDocument doc = JsonDocument.Parse(body);
        return ReadObject(doc.RootElement);
    }

    private static Dictionary<string, object?> ReadObject(JsonElement el)
    {
        var dict = new Dictionary<string, object?>();
        if (el.ValueKind != JsonValueKind.Object)
        {
            return dict;
        }
        foreach (JsonProperty prop in el.EnumerateObject())
        {
            dict[prop.Name] = ReadValue(prop.Value);
        }
        return dict;
    }

    private static object? ReadValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => ReadObject(el),
        JsonValueKind.Array => el.EnumerateArray().Select(ReadValue).ToList(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.ToString(),
    };
}
