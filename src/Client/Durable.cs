using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Visorcraft.MongrelDB;

/// <summary>
/// Structural hybrid logical clock from durable recovery (0.64+).
/// Parsed from the nested <c>last_commit_hlc</c> object on query status / cancel
/// responses — never reconstructed by string-parsing free-form status text.
/// </summary>
public sealed class CommitHlc
{
    [JsonPropertyName("physical_micros")]
    public ulong PhysicalMicros { get; set; }

    [JsonPropertyName("logical")]
    public uint Logical { get; set; }

    [JsonPropertyName("node_tiebreaker")]
    public uint NodeTiebreaker { get; set; }

    public CommitHlc()
    {
    }

    public CommitHlc(ulong physicalMicros, uint logical, uint nodeTiebreaker)
    {
        PhysicalMicros = physicalMicros;
        Logical = logical;
        NodeTiebreaker = nodeTiebreaker;
    }

    /// <summary>
    /// Decode a <c>last_commit_hlc</c> map; returns <see langword="null"/> when the shape is absent.
    /// </summary>
    public static CommitHlc? FromMap(object? raw)
    {
        if (raw is not IDictionary<string, object?> map)
        {
            // Also accept non-generic dictionary shapes from loose JSON trees.
            if (raw is not System.Collections.IDictionary idict)
            {
                return null;
            }
            object? physObj = idict.Contains("physical_micros") ? idict["physical_micros"] : null;
            if (physObj is null)
            {
                return null;
            }
            return new CommitHlc(
                ToUInt64(physObj) ?? 0UL,
                ToUInt32(idict.Contains("logical") ? idict["logical"] : null) ?? 0U,
                ToUInt32(idict.Contains("node_tiebreaker") ? idict["node_tiebreaker"] : null) ?? 0U);
        }

        if (!map.TryGetValue("physical_micros", out object? phys) || phys is null)
        {
            return null;
        }
        return new CommitHlc(
            ToUInt64(phys) ?? 0UL,
            ToUInt32(map.TryGetValue("logical", out object? l) ? l : null) ?? 0U,
            ToUInt32(map.TryGetValue("node_tiebreaker", out object? n) ? n : null) ?? 0U);
    }

    internal static ulong? ToUInt64(object? v)
    {
        return v switch
        {
            null => null,
            ulong u => u,
            long l when l >= 0 => (ulong)l,
            int i when i >= 0 => (ulong)i,
            uint u => u,
            decimal d when d >= 0 => (ulong)d,
            double db when db >= 0 && db <= ulong.MaxValue => (ulong)db,
            string s when ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong p) => p,
            IConvertible c => Convert.ToUInt64(c, CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    internal static uint? ToUInt32(object? v)
    {
        return v switch
        {
            null => null,
            uint u => u,
            int i when i >= 0 => (uint)i,
            long l when l >= 0 && l <= uint.MaxValue => (uint)l,
            ulong u when u <= uint.MaxValue => (uint)u,
            decimal d when d >= 0 && d <= uint.MaxValue => (uint)d,
            double db when db >= 0 && db <= uint.MaxValue => (uint)db,
            string s when uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint p) => p,
            IConvertible c => Convert.ToUInt32(c, CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    internal static long? ToInt64(object? v)
    {
        return v switch
        {
            null => null,
            long l => l,
            int i => i,
            ulong u when u <= long.MaxValue => (long)u,
            uint u => u,
            decimal d => (long)d,
            double db => (long)db,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long p) => p,
            IConvertible c => Convert.ToInt64(c, CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    internal static int? ToInt32(object? v)
    {
        return v switch
        {
            null => null,
            int i => i,
            long l when l >= int.MinValue && l <= int.MaxValue => (int)l,
            uint u when u <= int.MaxValue => (int)u,
            ulong ul when ul <= int.MaxValue => (int)ul,
            decimal d => (int)d,
            double db => (int)db,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) => p,
            IConvertible c => Convert.ToInt32(c, CultureInfo.InvariantCulture),
            _ => null,
        };
    }
}

/// <summary>
/// Nested durable recovery payload on query status / cancel responses
/// (parity with the server <c>DurableOutcome</c> / <c>outcome</c> JSON object).
/// </summary>
public sealed class DurableOutcome
{
    [JsonPropertyName("committed")]
    public bool? Committed { get; set; }

    [JsonPropertyName("committed_statements")]
    public int? CommittedStatements { get; set; }

    [JsonPropertyName("last_commit_epoch")]
    public ulong? LastCommitEpoch { get; set; }

    [JsonPropertyName("last_commit_epoch_text")]
    public string? LastCommitEpochText { get; set; }

    [JsonPropertyName("last_commit_hlc")]
    public CommitHlc? LastCommitHlc { get; set; }

    [JsonPropertyName("first_commit_statement_index")]
    public int? FirstCommitStatementIndex { get; set; }

    [JsonPropertyName("last_commit_statement_index")]
    public int? LastCommitStatementIndex { get; set; }

    [JsonPropertyName("completed_statements")]
    public int? CompletedStatements { get; set; }

    [JsonPropertyName("statement_index")]
    public int? StatementIndex { get; set; }

    [JsonPropertyName("serialization")]
    public string Serialization { get; set; } = "";

    [JsonPropertyName("serialization_state")]
    public string? SerializationState { get; set; }

    [JsonPropertyName("terminal_state")]
    public string? TerminalState { get; set; }

    /// <summary>Decode an <c>outcome</c> / <c>durable</c> object from a JSON map.</summary>
    public static DurableOutcome FromMap(object? raw)
    {
        if (raw is not System.Collections.IDictionary idict)
        {
            return new DurableOutcome();
        }

        object? Get(string key) => idict.Contains(key) ? idict[key] : null;

        return new DurableOutcome
        {
            Committed = Get("committed") as bool?,
            CommittedStatements = CommitHlc.ToInt32(Get("committed_statements")),
            LastCommitEpoch = CommitHlc.ToUInt64(Get("last_commit_epoch")),
            LastCommitEpochText = Get("last_commit_epoch_text")?.ToString(),
            LastCommitHlc = CommitHlc.FromMap(Get("last_commit_hlc")),
            FirstCommitStatementIndex = CommitHlc.ToInt32(Get("first_commit_statement_index")),
            LastCommitStatementIndex = CommitHlc.ToInt32(Get("last_commit_statement_index")),
            CompletedStatements = CommitHlc.ToInt32(Get("completed_statements")),
            StatementIndex = CommitHlc.ToInt32(Get("statement_index")),
            Serialization = Get("serialization")?.ToString() ?? "",
            SerializationState = Get("serialization_state")?.ToString(),
            TerminalState = Get("terminal_state")?.ToString(),
        };
    }
}

/// <summary>
/// Decoded <c>GET /queries/{query_id}</c> status for durable recovery (0.64+).
/// Prefer <see cref="GetCommitHlc"/> / <see cref="GetSerializationState"/> helpers —
/// they pick the nested durable / outcome fields over top-level duplicates when present.
/// </summary>
public sealed class QueryStatus
{
    [JsonPropertyName("query_id")]
    public string QueryId { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("server_state")]
    public string ServerState { get; set; } = "";

    [JsonPropertyName("terminal_state")]
    public string? TerminalState { get; set; }

    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    [JsonPropertyName("committed")]
    public bool? Committed { get; set; }

    [JsonPropertyName("committed_statements")]
    public int? CommittedStatements { get; set; }

    [JsonPropertyName("last_commit_epoch")]
    public ulong? LastCommitEpoch { get; set; }

    [JsonPropertyName("last_commit_epoch_text")]
    public string? LastCommitEpochText { get; set; }

    [JsonPropertyName("last_commit_hlc")]
    public CommitHlc? LastCommitHlc { get; set; }

    [JsonPropertyName("first_commit_statement_index")]
    public int? FirstCommitStatementIndex { get; set; }

    [JsonPropertyName("last_commit_statement_index")]
    public int? LastCommitStatementIndex { get; set; }

    [JsonPropertyName("completed_statements")]
    public int? CompletedStatements { get; set; }

    [JsonPropertyName("statement_index")]
    public int? StatementIndex { get; set; }

    [JsonPropertyName("cancel_outcome")]
    public string? CancelOutcome { get; set; }

    [JsonPropertyName("cancellation_reason")]
    public string? CancellationReason { get; set; }

    [JsonPropertyName("retryable")]
    public bool Retryable { get; set; }

    [JsonPropertyName("outcome")]
    public DurableOutcome Outcome { get; set; } = new();

    [JsonPropertyName("durable")]
    public DurableOutcome? Durable { get; set; }

    /// <summary>Original map when decoded via <see cref="FromMap"/>; otherwise empty.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, object?> Raw { get; set; }
        = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// Authoritative commit HLC: nested <c>durable</c> → nested <c>outcome</c> → top-level
    /// <c>last_commit_hlc</c>.
    /// </summary>
    public CommitHlc? GetCommitHlc()
    {
        if (Durable?.LastCommitHlc is not null)
        {
            return Durable.LastCommitHlc;
        }
        if (Outcome.LastCommitHlc is not null)
        {
            return Outcome.LastCommitHlc;
        }
        return LastCommitHlc;
    }

    /// <summary>
    /// Authoritative serialization state: nested durable/outcome
    /// <c>serialization_state</c>, then <c>serialization</c>.
    /// </summary>
    public string GetSerializationState()
    {
        if (Durable is not null)
        {
            if (!string.IsNullOrEmpty(Durable.SerializationState))
            {
                return Durable.SerializationState!;
            }
            if (!string.IsNullOrEmpty(Durable.Serialization))
            {
                return Durable.Serialization;
            }
        }
        if (!string.IsNullOrEmpty(Outcome.SerializationState))
        {
            return Outcome.SerializationState!;
        }
        return Outcome.Serialization ?? "";
    }

    /// <summary>Decode a query-status JSON object map.</summary>
    public static QueryStatus FromMap(IDictionary<string, object?>? raw)
    {
        raw ??= new Dictionary<string, object?>(StringComparer.Ordinal);
        object? Get(string key) => raw.TryGetValue(key, out object? v) ? v : null;

        DurableOutcome outcome = DurableOutcome.FromMap(Get("outcome"));
        DurableOutcome? durable = Get("durable") is System.Collections.IDictionary
            ? DurableOutcome.FromMap(Get("durable"))
            : null;

        string state = Get("state")?.ToString() ?? "";
        string serverState = Get("server_state")?.ToString() ?? state;

        return new QueryStatus
        {
            QueryId = Get("query_id")?.ToString() ?? "",
            Status = Get("status")?.ToString() ?? "",
            State = state,
            ServerState = serverState,
            TerminalState = Get("terminal_state")?.ToString(),
            Operation = Get("operation")?.ToString(),
            Committed = Get("committed") as bool?,
            CommittedStatements = CommitHlc.ToInt32(Get("committed_statements")),
            LastCommitEpoch = CommitHlc.ToUInt64(Get("last_commit_epoch")),
            LastCommitEpochText = Get("last_commit_epoch_text")?.ToString(),
            LastCommitHlc = CommitHlc.FromMap(Get("last_commit_hlc")),
            FirstCommitStatementIndex = CommitHlc.ToInt32(Get("first_commit_statement_index")),
            LastCommitStatementIndex = CommitHlc.ToInt32(Get("last_commit_statement_index")),
            CompletedStatements = CommitHlc.ToInt32(Get("completed_statements")),
            StatementIndex = CommitHlc.ToInt32(Get("statement_index")),
            CancelOutcome = Get("cancel_outcome")?.ToString(),
            CancellationReason = Get("cancellation_reason")?.ToString(),
            Retryable = Get("retryable") is bool b && b,
            Outcome = outcome,
            Durable = durable,
            Raw = new Dictionary<string, object?>(raw, StringComparer.Ordinal),
        };
    }

    /// <summary>Decode a query-status body from JSON bytes (test/helpers and HTTP path).</summary>
    public static QueryStatus ParseJson(byte[] data)
    {
        if (data is null || data.Length == 0)
        {
            return new QueryStatus();
        }
        var status = JsonSerializer.Deserialize<QueryStatus>(data, DurableJson.Options)
            ?? new QueryStatus();
        if (string.IsNullOrEmpty(status.ServerState) && !string.IsNullOrEmpty(status.State))
        {
            status.ServerState = status.State;
        }
        status.Outcome ??= new DurableOutcome();
        return status;
    }
}

internal static class DurableJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
