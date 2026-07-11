# Quickstart

Zero to a running MongrelDB .NET program in fifteen minutes. This guide walks
through installing the prerequisites, starting the daemon, and writing,
running, and understanding a complete async C# program.

---

## 1. Prerequisites

You need two things installed: the .NET 8 SDK and a `mongreldb-server`
daemon.

### Install the .NET 8 SDK

Verify it:

```sh
dotnet --version
# 8.0.x or newer
```

If you do not have it, install from <https://dotnet.microsoft.com/download>
or your package manager (e.g. `pacman -S dotnet-sdk`, `brew install --cask
dotnet-sdk`).

### Install mongreldb-server

Fetch a prebuilt server binary from the
[MongrelDB releases](https://github.com/visorcraft/MongrelDB/releases):

```sh
mkdir -p bin
curl -fsSL -o bin/mongreldb-server \
  https://github.com/visorcraft/MongrelDB/releases/download/v0.48.0/mongreldb-server-linux-x64
chmod +x bin/mongreldb-server
```

Verify it runs:

```sh
./bin/mongreldb-server --version
```

## 2. Start the daemon

By default `mongreldb-server` listens on `http://127.0.0.1:8453` and stores
data in the current working directory.

```sh
mkdir -p /tmp/mdb-data && cd /tmp/mdb-data
/path/to/mongreldb-server
```

In another terminal, sanity-check it:

```sh
curl http://127.0.0.1:8453/health
# ok
```

Leave the daemon running for the rest of this guide.

## 3. Create a project and pull in the client

```sh
dotnet new console -n MdbDemo
cd MdbDemo
dotnet add package Visorcraft.MongrelDB
```

The package targets `net8.0` and has no external NuGet dependencies.

## 4. Write your first program

Replace `Program.cs`:

```csharp
using Visorcraft.MongrelDB;

using var db = new MongrelDBClient("http://127.0.0.1:8453");

// 1. Health check before doing anything else.
if (!await db.HealthAsync())
{
    Console.Error.WriteLine("daemon not reachable");
    return 1;
}

// 2. Create a table. Each column is a Dictionary with id, name, ty, and flags.
//    The first column is the primary key. Column ids are stable on-wire
//    identifiers - use them everywhere else.
long tableId = await db.CreateTableAsync("orders", new[]
{
    Column(1, "id", "int64", primaryKey: true),
    Column(2, "customer", "varchar", primaryKey: false),
    Column(3, "amount", "float64", primaryKey: false),
});
Console.WriteLine($"created table id: {tableId}");

// 3. Insert rows. Cells.Of takes alternating column-id/value pairs.
//    idempotencyKey defaults to null (fine for a one-shot demo).
await db.PutAsync("orders", Cells.Of(1, 1L, 2, "Alice", 3, 99.5));
await db.PutAsync("orders", Cells.Of(1, 2L, 2, "Bob", 3, 150.0));

// 4. Query with a native index condition. The range index serves this in
//    sub-millisecond. Projection selects only column ids 1 and 2.
var q = db.Query("orders")
    .Where("range", new Dictionary<string, object?> { ["column"] = 3L, ["min"] = 100.0 })
    .Projection(new long[] { 1, 2 })
    .Limit(100);
List<Dictionary<string, object?>> rows = await q.ExecuteAsync();
foreach (var row in rows)
{
    Console.WriteLine($"row: {string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value}"))}");
}

// 5. Count the rows.
Console.WriteLine($"total rows: {await db.CountAsync("orders")}");
return 0;

// Helper: build a column descriptor for CreateTableAsync.
static Dictionary<string, object?> Column(long id, string name, string ty, bool primaryKey) =>
    new()
    {
        ["id"] = id,
        ["name"] = name,
        ["ty"] = ty,
        ["primary_key"] = primaryKey,
        ["nullable"] = false,
    };
```

Run it:

```sh
dotnet run
```

You should see:

```
created table id: 1
row: 2=Bob
total rows: 2
```

## 5. Enum, default value, and CHECK constraints

`CreateTableAsync` forwards every key in each column `Dictionary` straight
to the daemon's `/kit/create_table` endpoint. Two optional keys are
recognised on top of `id`, `name`, `ty`, `primary_key`, `nullable`:

| Key | Type | Effect |
|-----|------|--------|
| `enum_variants` | `string[]` | Required when `ty` is `"enum"`. Ordered list of allowed values. |
| `default_value` | JSON scalar | Static per-column default, preserving string/number/bool/null type. |
| `default_expr` | `string` | Dynamic default: `"now"` or `"uuid"`. |

Both arrive on the wire verbatim - the codec does not rename or strip them.

```csharp
await db.CreateTableAsync("users", new[]
{
    new Dictionary<string, object?>
    {
        ["id"] = 1L, ["name"] = "id", ["ty"] = "int64",
        ["primary_key"] = true,  ["nullable"] = false,
    },
    // Enum column with three allowed values and a default.
    new Dictionary<string, object?>
    {
        ["id"] = 2L, ["name"] = "role", ["ty"] = "enum",
        ["primary_key"] = false, ["nullable"] = false,
        ["enum_variants"] = new[] { "admin", "user", "guest" },
        ["default_value"] = "guest",
    },
    // Integer column with a static numeric default.
    new Dictionary<string, object?>
    {
        ["id"] = 3L, ["name"] = "score", ["ty"] = "int64",
        ["primary_key"] = false, ["nullable"] = false,
        ["default_value"] = 7,
    },
    // Boolean column with a static true default.
    new Dictionary<string, object?>
    {
        ["id"] = 4L, ["name"] = "active", ["ty"] = "bool",
        ["primary_key"] = false, ["nullable"] = false,
        ["default_value"] = true,
    },
    // Nullable column with an explicit null default.
    new Dictionary<string, object?>
    {
        ["id"] = 5L, ["name"] = "optional", ["ty"] = "varchar",
        ["primary_key"] = false, ["nullable"] = true,
        ["default_value"] = null,
    },
    // Timestamp column that fills in "now" on insert.
    // This is a dynamic default, so it uses default_expr, not default_value.
    new Dictionary<string, object?>
    {
        ["id"] = 6L, ["name"] = "created_at", ["ty"] = "timestamp_nanos",
        ["primary_key"] = false, ["nullable"] = false,
        ["default_expr"] = "now",
    },
    // UUID column with a dynamic "uuid" default.
    new Dictionary<string, object?>
    {
        ["id"] = 7L, ["name"] = "uuid_col", ["ty"] = "uuid",
        ["primary_key"] = false, ["nullable"] = false,
        ["default_expr"] = "uuid",
    },
});
```

CHECK constraints (regex, range, equality, boolean composition) live in a
top-level `constraints` block on the same payload - the on-wire shape is
shown in the README's [Schema constraints](../README.md#schema-constraints)
section.

## 6. What each part does

| Code | What it does |
|------|--------------|
| `new MongrelDBClient(url)` | Builds an HTTP client targeting one daemon. Thread-safe once constructed; `IDisposable`. |
| `await db.HealthAsync()` | GET `/health`; returns `true` when the daemon answers (swallows errors). |
| `await db.CreateTableAsync(name, columns)` | POST `/kit/create_table`. Column `id`s are the on-wire identifiers; use them everywhere else. |
| `await db.PutAsync(table, cells, key)` | Single-op transaction: POST `/kit/txn` with one `put` op. `cells` is flattened to `[col_id, val, ...]`. |
| `db.Query(table).Where(...)` | Builds a `/kit/query` body. `Where` pushes a condition down to a native index. |
| `.Projection(new long[] { 1, 2 })` | Server returns only those column ids, saving bandwidth. |
| `.Limit(100)` | Caps the result; check `q.Truncated` afterward to detect overflow. |
| `await q.ExecuteAsync()` | Sends the query and decodes the `rows` list. |
| `await db.CountAsync(table)` | GET `/tables/{name}/count`. |

## 7. Common pitfalls

**Using the column name instead of the column id.** Every on-wire API uses
the numeric `id` from `CreateTableAsync`, never the `name`. The query
builder's `column` alias maps to the server's `column_id` - pass the `long`
id, not the string name:

```csharp
// Wrong:
.Where("range", new Dictionary<string, object?> { ["column"] = "amount", ["min"] = 100.0 })
// Right:
.Where("range", new Dictionary<string, object?> { ["column"] = 3L, ["min"] = 100.0 })
```

**Forgetting the `L` suffix on long values.** `Cells.Of(1, 2L, ...)` boxes to
`long`; `Cells.Of(1, 2, ...)` boxes to `int` (the factory still converts it,
but explicit `L`s make intent clear and avoid surprises in dictionaries
typed as `object?`). Projection must be `long[]`, so write `new long[] { 1, 2 }`.

**Treating a single `PutAsync` as non-transactional.** `PutAsync` is a one-op
transaction. A unique constraint violation throws `ConflictException` (HTTP
409), not a silent no-op.

**Calling `CommitAsync` twice on the same `Transaction`.** The second call
throws `InvalidOperationException`. Create a fresh `db.BeginTransaction()` for
each logical unit of work.

**Forgetting to `await`.** All I/O methods are async and return `Task`/`Task<T>`.
A fire-and-forget `db.PutAsync(...)` without `await` will race disposal of the
client and surface errors unpredictably. Always `await`.

**Disposing the client too early.** `MongrelDBClient` is `IDisposable`. Use a
`using`/`using var` scope that outlives every outstanding call.

**Reusing a `QueryBuilder` and expecting a fresh `Truncated`.** `Truncated`
reflects the most recent `ExecuteAsync`. Build a new query, or re-run
`ExecuteAsync` before reading it.

**Expecting `SqlAsync` to always return rows.** The `/sql` endpoint streams
Arrow IPC for `SELECT` in most builds, so `SqlAsync` returns an empty list
(not an exception) for result sets. Use it for DDL/DML and statements whose
success is the signal; use the native query builder for typed row retrieval.

**Pointing at a daemon that requires auth.** If the daemon was started with
`--auth-token` or `--auth-users`, every call throws `AuthException` unless
you construct the client with a token or Basic credentials. See
[auth.md](auth.md).

## 8. History retention

Administrators can inspect and adjust how many committed epochs the engine
retains for historical (`AS OF EPOCH`) reads. The daemon defaults to 1024
retained epochs.

```csharp
HistoryRetention current = await db.GetHistoryRetentionAsync();
ulong epochs = await db.HistoryRetentionEpochsAsync();
ulong earliest = await db.EarliestRetainedEpochAsync();
await db.SetHistoryRetentionEpochsAsync(1000);
```

`GetHistoryRetentionAsync` returns a `HistoryRetention` record with
`HistoryRetentionEpochs` and `EarliestRetainedEpoch`. The individual getters
`HistoryRetentionEpochsAsync` and `EarliestRetainedEpochAsync` return a single
`ulong`. All three routes require `ADMIN` permission when catalog authentication
is enabled. Increasing the retention window **cannot restore history that was
already pruned**.

## Next steps

- [transactions.md](transactions.md) - atomic batches, idempotency, retries
- [queries.md](queries.md) - every native index condition
- [sql.md](sql.md) - recursive CTEs, window functions, `CREATE TABLE AS SELECT`
- [auth.md](auth.md) - bearer tokens, basic auth, user/role management
- [errors.md](errors.md) - the full exception hierarchy and recovery patterns
