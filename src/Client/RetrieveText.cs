using System.Text.Json.Serialization;

namespace Visorcraft.MongrelDB;

/// <summary>
/// Result of <c>POST /kit/retrieve_text</c> (0.64+): text → embed → ANN retrieve.
/// </summary>
public sealed class TextRetrieveResult
{
    [JsonPropertyName("hits")]
    public List<Dictionary<string, object?>> Hits { get; set; } = new();

    [JsonPropertyName("provenance")]
    public Dictionary<string, object?> Provenance { get; set; } = new();
}

/// <summary>Optional knobs for <see cref="MongrelDBClient.RetrieveTextAsync"/>.</summary>
public sealed class RetrieveTextOptions
{
    /// <summary>Top-k hits to return. Omitted from the wire body when ≤ 0.</summary>
    public int K { get; set; }

    /// <summary>Optional deadline in milliseconds.</summary>
    public ulong? DeadlineMs { get; set; }

    /// <summary>Optional max work budget.</summary>
    public ulong? MaxWork { get; set; }
}

/// <summary>
/// Builds and documents the wire shape for <c>POST /kit/retrieve_text</c>.
/// </summary>
public static class RetrieveText
{
    /// <summary>
    /// Returns the JSON body for <c>POST /kit/retrieve_text</c>.
    /// </summary>
    /// <exception cref="ArgumentException">When <paramref name="table"/> or <paramref name="text"/> is empty.</exception>
    public static Dictionary<string, object?> BuildRequest(
        string table,
        int embeddingColumn,
        string text,
        RetrieveTextOptions? opts = null)
    {
        if (string.IsNullOrEmpty(table))
        {
            throw new ArgumentException("table is required", nameof(table));
        }
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("text is required", nameof(text));
        }

        var payload = new Dictionary<string, object?>
        {
            ["table"] = table,
            ["embedding_column"] = embeddingColumn,
            ["text"] = text,
        };
        if (opts is not null)
        {
            if (opts.K > 0)
            {
                payload["k"] = opts.K;
            }
            if (opts.DeadlineMs is not null)
            {
                payload["deadline_ms"] = opts.DeadlineMs.Value;
            }
            if (opts.MaxWork is not null)
            {
                payload["max_work"] = opts.MaxWork.Value;
            }
        }
        return payload;
    }
}
