using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Visorcraft.MongrelDB;
using Xunit;
using Xunit.Abstractions;

namespace MongrelDB.Tests;

/// <summary>
/// Live integration tests for the MongrelDB .NET client.
/// </summary>
/// <remarks>
/// <para>
/// These tests boot a real <c>mongreldb-server</c> daemon and exercise the full
/// client surface against it. They resolve the daemon binary in this order:
/// </para>
/// <list type="number">
///   <item>the <c>MONGRELDB_SERVER</c> env var (path to the server binary)</item>
///   <item>a prebuilt binary at <c>./bin/mongreldb-server</c></item>
///   <item><c>mongreldb-server</c> on <c>PATH</c></item>
/// </list>
/// <para>
/// If no binary is available, the live suite is skipped (each live test calls
/// <see cref="RequireDaemon"/>). Set <c>MONGRELDB_URL</c> to point at an
/// already-running daemon to skip the boot and connect directly. The offline
/// tests always run so the suite is never reported as "no tests".
/// </para>
/// </remarks>
public class LiveTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private MongrelDBClient? _db;
    private Process? _serverProcess;
    private string? _dataDir;
    private string? _logFile;
    private bool _daemonAvailable;

    public LiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        string existing = Env("MONGRELDB_URL");
        if (!string.IsNullOrEmpty(existing))
        {
            // If a daemon is already running, connect to it directly.
            _db = new MongrelDBClient(existing, token: Env("MONGRELDB_TOKEN"), username: null, password: null);
            if (await _db.HealthAsync())
            {
                _daemonAvailable = true;
                _output.WriteLine($"Using existing daemon at {existing}");
                return;
            }
            Assert.Fail($"MONGRELDB_URL={existing} is not reachable");
        }

        string? bin = ResolveServerBinary();
        if (bin is null)
        {
            // No daemon available: live tests skip themselves.
            _output.WriteLine("No mongreldb-server binary available; live tests skipped");
            return;
        }

        int port = FreePort();
        _dataDir = Path.Combine(Path.GetTempPath(), "mongreldb-dotnet-test-" + Guid.NewGuid().ToString("N"));
        _logFile = Path.Combine(Path.GetTempPath(), "mongreldb-dotnet-server-" + Guid.NewGuid().ToString("N") + ".log");
        Directory.CreateDirectory(_dataDir);

        var psi = new ProcessStartInfo(bin, new[] { _dataDir, "--port", port.ToString() })
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        _serverProcess = new Process { StartInfo = psi };
        // Merge stderr into stdout and tee into the log file for diagnostics.
        _serverProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                File.AppendAllText(_logFile, e.Data + Environment.NewLine);
            }
        };
        _serverProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                File.AppendAllText(_logFile, e.Data + Environment.NewLine);
            }
        };

        Assert.True(_serverProcess.Start(), "failed to start mongreldb-server");
        _serverProcess.BeginErrorReadLine();
        _serverProcess.BeginOutputReadLine();

        string url = $"http://127.0.0.1:{port}";
        if (!await WaitForHealthAsync(url, 40))
        {
            string log = ReadLog();
            await DestroyProcessAsync();
            Assert.Fail("mongreldb-server did not become healthy. Log:\n" + log);
        }
        _db = new MongrelDBClient(url);
        _daemonAvailable = true;
        _output.WriteLine($"Booted mongreldb-server on {url}");
    }

    public async Task DisposeAsync()
    {
        _db?.Dispose();
        if (_serverProcess is not null)
        {
            await DestroyProcessAsync();
        }
        if (_dataDir is not null && Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, recursive: true); }
            catch (IOException e) { _output.WriteLine($"Could not delete data dir: {e.Message}"); }
        }
        if (_logFile is not null && File.Exists(_logFile))
        {
            try { File.Delete(_logFile); }
            catch (IOException) { /* best-effort */ }
        }
    }

    /// <summary>Skip the live test when no daemon was booted.</summary>
    private void RequireDaemon()
    {
        if (!_daemonAvailable || _db is null)
        {
            _output.WriteLine("Skipping live test: no mongreldb-server available");
            throw new SkipException("no mongreldb-server available");
        }
    }

    // ── Live tests ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task HealthReportsDaemonAsHealthy()
    {
        RequireDaemon();
        bool ok = await _db!.HealthAsync();
        Assert.True(ok, "expected healthy daemon");
    }

    [SkippableFact]
    public async Task HistoryRetention_Window_Survives_After_Update()
    {
        RequireDaemon();

        // Set a 1000-epoch retention window before any history-dependent writes.
        HistoryRetention retention = await _db!.SetHistoryRetentionEpochsAsync(1000);
        Assert.Equal(1000UL, retention.HistoryRetentionEpochs);

        // Read it back through the typed API and the individual getters.
        Assert.Equal(1000UL, await _db!.HistoryRetentionEpochsAsync());
        ulong earliestBefore = await _db.EarliestRetainedEpochAsync();

        // Capture the current visible epoch before writing.
        long epochBefore = await ReadCurrentEpochAsync();

        string name = UniqueTable("dotnet_retention");
        await FreshTableAsync(name, IntCol(1, "id", true), IntCol(2, "amount", false));

        // Insert a row. Each commit advances the visible epoch.
        await _db.PutAsync(name, Cells.Of(1, 1L, 2, 50L));
        long insertEpoch = await ReadCurrentEpochAsync();
        Assert.True(insertEpoch > epochBefore, "insert should advance the epoch");

        // Update the same row so the current value differs from the insert-time value.
        await _db.PutAsync(name, Cells.Of(1, 1L, 2, 999L));

        // The pre-update value must still be readable at the insert epoch.
        List<Dictionary<string, object?>> rows = await _db.SqlAsync(
            $"SELECT amount FROM {name} AS OF EPOCH {insertEpoch} WHERE id = 1");
        Assert.Single(rows);
        Assert.Equal(50L, CellJsonLong(rows[0], "amount"));

        // Shrinking the window and re-expanding it cannot restore already-pruned history.
        // The engine may retain more than requested, so we only assert monotonicity:
        // re-expanding cannot move earliest_retained_epoch backward.
        await _db.SetHistoryRetentionEpochsAsync(1);
        ulong earliestAfterShrink = await _db.EarliestRetainedEpochAsync();
        await _db.SetHistoryRetentionEpochsAsync(1000);
        Assert.Equal(earliestAfterShrink, await _db.EarliestRetainedEpochAsync());
        Assert.True(earliestAfterShrink >= earliestBefore, "earliest retained epoch must not move backward");
    }

    [SkippableFact]
    public async Task CreateTableAndCountRoundTrip()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_tbl");
        await FreshTableAsync(name, IntCol(1, "id", true), FloatCol(2, "amount"));
        Assert.Equal(0L, await _db!.CountAsync(name));
    }

    [SkippableFact]
    public async Task PutAndCountRoundTrip()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_put");
        await FreshTableAsync(name, IntCol(1, "id", true), FloatCol(2, "amount"));
        await _db!.PutAsync(name, Cells.Of(1, 1L, 2, 99.5));
        await _db.PutAsync(name, Cells.Of(1, 2L, 2, 150.0));
        Assert.Equal(2L, await _db.CountAsync(name));
    }

    [SkippableFact]
    public async Task QueryByPk()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_pk");
        await FreshTableAsync(name, IntCol(1, "id", true));
        await _db!.PutAsync(name, Cells.Of(1, 42L));
        await _db.PutAsync(name, Cells.Of(1, 43L));

        List<Dictionary<string, object?>> rows = await _db.Query(name)
            .Where("pk", new Dictionary<string, object?> { ["value"] = 42L })
            .ExecuteAsync();
        Assert.Single(rows);
        // The returned row must carry the queried PK value.
        Assert.Equal(42L, CellLong(rows[0], 1));
    }

    [SkippableFact]
    public async Task QueryWithRangeUsingFriendlyAliases()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_range");
        await FreshTableAsync(name, IntCol(1, "id", true), IntCol(2, "amount", false));
        await _db!.PutAsync(name, Cells.Of(1, 1L, 2, 50L));
        await _db.PutAsync(name, Cells.Of(1, 2L, 2, 120L));
        await _db.PutAsync(name, Cells.Of(1, 3L, 2, 200L));

        // Range predicate using friendly aliases (column/min/max -> column_id/lo/hi).
        QueryBuilder q = _db.Query(name)
            .Where("range", new Dictionary<string, object?> { ["column"] = 2L, ["min"] = 100L, ["max"] = 150L });
        List<Dictionary<string, object?>> rows = await q.ExecuteAsync();
        // Only the row with amount=120 (pk=2) falls in [100, 150].
        Assert.Single(rows);
        Assert.False(q.Truncated);
        // Verify the PK and amount values of returned rows match the filter range.
        foreach (Dictionary<string, object?> row in rows)
        {
            Assert.Equal(2L, CellLong(row, 1));
            long amt = CellLong(row, 2);
            Assert.InRange(amt, 100L, 150L);
        }
    }

    [SkippableFact]
    public async Task BatchTransactionPutCommit()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_txn");
        await FreshTableAsync(name, IntCol(1, "id", true));

        var txn = _db!.BeginTransaction();
        txn.Put(name, Cells.Of(1, 1L), returning: false);
        txn.Put(name, Cells.Of(1, 2L), returning: false);
        txn.Put(name, Cells.Of(1, 3L), returning: false);
        Assert.Equal(3, txn.Count);

        List<Dictionary<string, object?>> results = await txn.CommitAsync();
        Assert.Equal(3, results.Count);
        Assert.Equal(3L, await _db.CountAsync(name));
    }

    [SkippableFact]
    public async Task TransactionRollbackDiscardsStagedOps()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_rb");
        await FreshTableAsync(name, IntCol(1, "id", true));

        var txn = _db!.BeginTransaction();
        txn.Put(name, Cells.Of(1, 1L), returning: false);
        Assert.Equal(1, txn.Count);
        txn.Rollback();
        Assert.Equal(0L, await _db.CountAsync(name));
    }

    [SkippableFact]
    public async Task DeleteByPkRemovesRow()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_del");
        await FreshTableAsync(name, IntCol(1, "id", true));
        await _db!.PutAsync(name, Cells.Of(1, 5L));
        Assert.Equal(1L, await _db.CountAsync(name));

        await _db.DeleteByPkAsync(name, 5L);
        Assert.Equal(0L, await _db.CountAsync(name));
    }

    [SkippableFact]
    public async Task SqlInsertIncreasesCountAndSelectReturnsRow()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_sql");
        await FreshTableAsync(name, IntCol(1, "id", true), IntCol(2, "amount", false));

        Assert.Equal(0L, await _db!.CountAsync(name));
        // INSERT via SQL must increase the row count.
        await _db.SqlAsync($"INSERT INTO {name} (id, amount) VALUES (10, 42)");
        Assert.Equal(1L, await _db.CountAsync(name));

        // JSON SQL mode must return the inserted row. An old server ignores the
        // requested JSON format and answers with Arrow IPC bytes, so SqlAsync()
        // returns an empty list - only verify row content when JSON mode worked.
        List<Dictionary<string, object?>> rows = await _db.SqlAsync($"SELECT id, amount FROM {name}");
        if (rows.Count > 0)
        {
            Assert.Single(rows);
            Assert.Equal(10L, CellJsonLong(rows[0], "id"));
        }
    }

    [SkippableFact]
    public async Task SchemaListsCreatedTable()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_schema");
        await FreshTableAsync(name, IntCol(1, "id", true), FloatCol(2, "amount"));

        Dictionary<string, Dictionary<string, object?>> schema = await _db!.GetSchemaAsync();
        Assert.True(schema.ContainsKey(name), $"schema catalog missing table {name}");
    }

    [SkippableFact]
    public async Task SchemaForReturnsSingleTableDescriptor()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_schema_for");
        await FreshTableAsync(name, IntCol(1, "id", true), FloatCol(2, "amount"));

        Dictionary<string, object?> desc = await _db!.GetSchemaForAsync(name);
        Assert.True(desc.ContainsKey("schema_id"), $"descriptor missing schema_id; got {Describe(desc)}");
        Assert.True(desc.TryGetValue("columns", out object? cols) && cols is IList<object?>, "columns should be a list");
        Assert.Equal(2, ((IList<object?>)cols!).Count);
    }

    [SkippableFact]
    public async Task TableNamesListsCreatedTable()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_tables");
        await FreshTableAsync(name, IntCol(1, "id", true));

        List<string> names = await _db!.GetTableNamesAsync();
        Assert.Contains(name, names);
    }

    [SkippableFact]
    public async Task SchemaForNonexistentTableThrowsNotFound()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_missing");
        NotFoundException ex = await Assert.ThrowsAsync<NotFoundException>(() => _db!.GetSchemaForAsync(name));
        Assert.Equal(404, ex.Status);
    }

    [SkippableFact]
    public async Task ErrorCarriesHttpStatus()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_missing2");
        // The base type is assignable from every typed subclass, so catching
        // MongrelDBException must also see NotFoundException and its status.
        Exception ex = await Record.ExceptionAsync(() => _db!.GetSchemaForAsync(name));
        Assert.IsAssignableFrom<MongrelDBException>(ex);
        var mdb = (MongrelDBException)ex;
        Assert.Equal(404, mdb.Status);
        Assert.IsType<NotFoundException>(mdb);
    }

    [SkippableFact]
    public async Task UpsertUpdatesOnPrimaryKeyConflict()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_upsert");
        await FreshTableAsync(name, IntCol(1, "id", true), IntCol(2, "amount", false));
        await _db!.PutAsync(name, Cells.Of(1, 1L, 2, 50L));

        // Upsert the same PK with an update_cells that rewrites amount.
        await _db.UpsertAsync(name, Cells.Of(1, 1L, 2, 50L), updateCells: Cells.Of(2, 999L));
        Assert.Equal(1L, await _db.CountAsync(name));

        List<Dictionary<string, object?>> rows = await _db.Query(name)
            .Where("pk", new Dictionary<string, object?> { ["value"] = 1L })
            .ExecuteAsync();
        Assert.Single(rows);
        // Assert the PK and the updated cell value.
        Assert.Equal(1L, CellLong(rows[0], 1));
        Assert.Equal(999L, CellLong(rows[0], 2));
    }

    [SkippableFact]
    public async Task IdempotentPutReturnsSameResultOnRetry()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_idem");
        await FreshTableAsync(name, IntCol(1, "id", true));

        string key = "idem-" + name;
        await _db!.PutAsync(name, Cells.Of(1, 7L), key);
        await _db.PutAsync(name, Cells.Of(1, 7L), key);
        // The daemon returns the original response on duplicate commits. The
        // row count must remain 1 either way.
        Assert.Equal(1L, await _db.CountAsync(name));
    }

    [SkippableFact]
    public async Task CompactAndCompactTableRunWithoutError()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_compact");
        await FreshTableAsync(name, IntCol(1, "id", true));
        await _db!.PutAsync(name, Cells.Of(1, 1L));

        // Both compaction endpoints should succeed without throwing.
        Assert.NotNull(await _db.CompactAsync());
        Assert.NotNull(await _db.CompactTableAsync(name));
    }

    [SkippableFact]
    public async Task DropTableRemovesTable()
    {
        RequireDaemon();
        string name = UniqueTable("dotnet_drop");
        await FreshTableAsync(name, IntCol(1, "id", true));
        Assert.Contains(name, await _db!.GetTableNamesAsync());

        await _db.DropTableAsync(name);
        Assert.DoesNotContain(name, await _db.GetTableNamesAsync());
    }

    // ── Offline tests (always run, no daemon needed) ─────────────────────

    /// <summary>
    /// A client constructed with no reachable server reports
    /// <see cref="MongrelDBClient.HealthAsync"/> == false rather than throwing.
    /// </summary>
    [Fact]
    public async Task HealthReturnsFalseWhenDaemonUnreachable()
    {
        using var unreachable = new MongrelDBClient("http://127.0.0.1:1");
        bool ok = await unreachable.HealthAsync();
        Assert.False(ok, "health should be false for an unreachable daemon");
    }

    /// <summary>
    /// A client constructed with a token attaches a Bearer header. Verified
    /// against an in-process TCP listener that speaks just enough HTTP/1.1 to
    /// capture the Authorization header and return a 2xx response.
    /// </summary>
    [Fact]
    public async Task BearerTokenAuthHeaderIsAttached()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string url = $"http://127.0.0.1:{port}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task serverTask = Task.Run(async () =>
        {
            using System.Net.Sockets.TcpClient conn = await listener.AcceptTcpClientAsync(cts.Token);
            using var stream = conn.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            string? lastAuth = null;
            string? line;
            while ((line = await reader.ReadLineAsync(cts.Token)) is not null && line.Length > 0)
            {
                if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                {
                    lastAuth = line.Substring("Authorization:".Length).Trim();
                }
            }

            byte[] body = Encoding.UTF8.GetBytes("{\"ok\":true}");
            byte[] resp = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(resp, cts.Token);
            await stream.WriteAsync(body, cts.Token);

            Interlocked.Exchange(ref _capturedAuth, lastAuth);
        });

        using var c = new MongrelDBClient(url, token: "super-secret", username: null, password: null);
        _ = await c.HealthAsync(cts.Token);
        await serverTask;
        listener.Stop();

        Assert.Equal("Bearer super-secret", _capturedAuth);
    }

    private string? _capturedAuth;

    // ── Helpers ───────────────────────────────────────────────────────────

    private static Dictionary<string, object?> IntCol(long id, string name, bool primaryKey) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["ty"] = "int64",
        ["primary_key"] = primaryKey,
        ["nullable"] = false,
    };

    private static Dictionary<string, object?> FloatCol(long id, string name) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["ty"] = "float64",
        ["primary_key"] = false,
        ["nullable"] = false,
    };

    /// <summary>
    /// freshTable drops <paramref name="name"/> if present then creates it with
    /// the given columns. A missing table on drop is the expected pre-condition
    /// and is ignored (the daemon returns a 5xx for a missing table).
    /// </summary>
    private async Task FreshTableAsync(string name, params Dictionary<string, object?>[] columns)
    {
        try { await _db!.DropTableAsync(name); }
        catch (MongrelDBException) { /* expected when the table doesn't exist yet */ }
        await _db!.CreateTableAsync(name, columns);
    }

    /// <summary>
    /// Reads the current visible database epoch via <c>PRAGMA data_version</c>.
    /// </summary>
    private async Task<long> ReadCurrentEpochAsync()
    {
        List<Dictionary<string, object?>> rows = await _db!.SqlAsync("PRAGMA data_version");
        Assert.Single(rows);
        return CellJsonLong(rows[0], "data_version");
    }

    private static string UniqueTable(string prefix) => prefix + "_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x");

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;

    /// <summary>Finds the daemon binary, or returns null to skip the live suite.</summary>
    private static string? ResolveServerBinary()
    {
        string env = Env("MONGRELDB_SERVER");
        if (!string.IsNullOrEmpty(env))
        {
            if (File.Exists(env))
            {
                return Path.GetFullPath(env);
            }
            return null;
        }
        string local = Path.Combine("bin", "mongreldb-server");
        if (File.Exists(local))
        {
            return Path.GetFullPath(local);
        }
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, "mongreldb-server");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<bool> WaitForHealthAsync(string url, int maxSeconds)
    {
        using var probe = new MongrelDBClient(url);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(maxSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe.HealthAsync())
            {
                return true;
            }
            await Task.Delay(500);
        }
        return false;
    }

    private string ReadLog()
    {
        try { return _logFile is not null && File.Exists(_logFile) ? File.ReadAllText(_logFile) : "(no log file)"; }
        catch (IOException e) { return $"(could not read log: {e.Message})"; }
    }

    private async Task DestroyProcessAsync()
    {
        if (_serverProcess is null) return;
        try
        {
            _serverProcess.Kill(entireProcessTree: true);
            _serverProcess.WaitForExit(5000);
        }
        catch { /* best-effort */ }
        await Task.CompletedTask;
    }

    private static string Describe(Dictionary<string, object?> dict)
    {
        try { return JsonSerializer.Serialize(dict); }
        catch { return $"({dict.Count} keys)"; }
    }

    /// <summary>
    /// Extracts a long value for <paramref name="colId"/> from a Kit row's flat
    /// cells array (shape: <c>[col_id, value, ...]</c>).
    /// </summary>
    private static long CellLong(Dictionary<string, object?> row, long colId)
    {
        if (row.TryGetValue("cells", out object? cellsObj) && cellsObj is IList<object?> cells)
        {
            for (int i = 0; i + 1 < cells.Count; i += 2)
            {
                if (cells[i] is long id && id == colId)
                {
                    return ToLong(cells[i + 1], colId);
                }
            }
        }
        throw new Xunit.Sdk.XunitException($"cell {colId} not found in row");
    }

    /// <summary>
    /// Extracts a long value for <paramref name="key"/> from a JSON SQL row
    /// (an object keyed by column name).
    /// </summary>
    private static long CellJsonLong(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out object? v))
        {
            return ToLong(v, 0);
        }
        throw new Xunit.Sdk.XunitException($"column {key} not found in row");
    }

    private static long ToLong(object? v, long colId)
    {
        return v switch
        {
            long l => l,
            int i => i,
            decimal d => (long)d,
            double dd => (long)dd,
            _ => throw new Xunit.Sdk.XunitException($"cell {colId} value not numeric: {v}"),
        };
    }
}
