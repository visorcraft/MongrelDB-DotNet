<p align="center">
  <img src="assets/mongrel.png" alt="MongrelDB logo" width="250" />
</p>

<h1 align="center">MongrelDB .NET Client</h1>

<p align="center">
  <b>C#/.NET client for MongrelDB - embedded+server database with SQL, vector search, full-text search, and AI-native retrieval.</b>
  <br />
  No external dependencies - built on the standard library <code>System.Net.Http.HttpClient</code> and <code>System.Text.Json</code> (.NET 8+). The API mirrors the MongrelDB PHP, Go, and Java clients.
</p>

<p align="center">
  <a href="https://github.com/visorcraft/MongrelDB-DotNet/actions/workflows/ci.yml"><img src="https://github.com/visorcraft/MongrelDB-DotNet/actions/workflows/ci.yml/badge.svg" alt=".NET CI" /></a>
  <a href="https://www.nuget.org/packages/Visorcraft.MongrelDB"><img src="https://img.shields.io/nuget/v/Visorcraft.MongrelDB.svg?label=NuGet" alt="NuGet" /></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-8.0%2B-512BD4.svg" alt=".NET" /></a>
  <a href="#license"><img src="https://img.shields.io/badge/license-MIT%20OR%20Apache--2.0-blue.svg" alt="License" /></a>
</p>

## Package

| Surface | Package | Install |
|---|---|---|
| .NET client | `Visorcraft.MongrelDB` | `dotnet add package Visorcraft.MongrelDB` |

### .NET CLI

```sh
dotnet add package Visorcraft.MongrelDB
```

### PackageReference

```xml
<PackageReference Include="Visorcraft.MongrelDB" Version="0.61.1" />
```

### Package Manager

```powershell
Install-Package Visorcraft.MongrelDB
```

The package has no runtime dependencies - only the .NET base class library.

## Requirements

- **.NET 8.0 or newer**
- A running [`mongreldb-server`](https://github.com/visorcraft/MongrelDB) daemon

## What It Provides

- **Typed CRUD** over the Kit transaction endpoint: `PutAsync`, `UpsertAsync` (insert-or-update on PK conflict), `DeleteAsync` by row id or `DeleteByPkAsync` by primary key, all with optional idempotency keys for safe retries.
- **Fluent query builder** that pushes conditions down to the engine's specialized indexes for sub-millisecond lookups: bitmap equality/IN, learned-range, null checks, FM-index full-text search, HNSW vector similarity (`ann`), and sparse vector match. Friendly aliases (`column` → `column_id`, `min`/`max` → `lo`/`hi`) are translated to the server's on-wire keys.
- **Idempotent batch transactions** - operations staged locally and committed atomically, with the engine enforcing unique, foreign-key, and check constraints at commit time. Idempotency keys return the original response on duplicate commits, even after a crash.
- **Full SQL access** through the DataFusion-backed `/sql` endpoint: recursive CTEs, window functions, `CREATE TABLE AS SELECT`, materialized views, and multi-statement execution.
- **Schema management**: typed table creation, full schema catalog, and per-table descriptors.
- **User/role/credentials management** via SQL: Argon2id-hashed catalog users, roles, and `GRANT`/`REVOKE` table-level permissions, all executed through `SqlAsync`.
- **Maintenance**: compaction (all tables or per-table).
- **Pluggable transport**: bring your own `HttpClient`. Bearer token and HTTP Basic auth are first-class options.
- **Typed errors**: `AuthException` (401/403), `NotFoundException` (404), `ConflictException` (409, with error code + op index), and `QueryException` (everything else), all extending `MongrelDBException` and carrying the status code and decoded server envelope.

## Examples

Task-focused, commented guides live in [`docs/`](docs):

- [Quickstart](docs/quickstart.md) - install, start the daemon, write and run a complete program.
- [Transactions](docs/transactions.md) - batch commits, idempotency keys, constraint handling.
- [Queries](docs/queries.md) - every native condition type and the index it pushes down to.
- [SQL](docs/sql.md) - recursive CTEs, window functions, advanced SQL.
- [Authentication](docs/auth.md) - Bearer token, HTTP Basic, and open modes.
- [Errors](docs/errors.md) - the exception hierarchy and recovery patterns.

## Quick Example

```csharp
using Visorcraft.MongrelDB;

// Connect to a running mongreldb-server daemon.
using var db = new MongrelDBClient("http://127.0.0.1:8453");

// Create a table. Column ids are stable on-wire identifiers.
await db.CreateTableAsync("orders", new[]
{
    new Dictionary<string, object?>
    {
        ["id"] = 1L, ["name"] = "id",       ["ty"] = "int64",   ["primary_key"] = true,  ["nullable"] = false,
    },
    new Dictionary<string, object?>
    {
        ["id"] = 2L, ["name"] = "customer", ["ty"] = "varchar", ["primary_key"] = false, ["nullable"] = false,
    },
    new Dictionary<string, object?>
    {
        ["id"] = 3L, ["name"] = "amount",   ["ty"] = "float64", ["primary_key"] = false, ["nullable"] = false,
    },
});

// Insert rows (cells map column id -> value).
await db.PutAsync("orders", Cells.Of(1, 1L, 2, "Alice", 3, 99.50));
await db.PutAsync("orders", Cells.Of(1, 2L, 2, "Bob",   3, 150.00));

// Upsert (insert or update on PK conflict).
await db.UpsertAsync("orders",
    cells: Cells.Of(1, 1L, 2, "Alice", 3, 120.00),
    updateCells: Cells.Of(3, 120.00));

// Query with a native index condition (learned-range index).
List<Dictionary<string, object?>> rows = await db.Query("orders")
    .Where("range", new Dictionary<string, object?> { ["column"] = 3L, ["min"] = 100.0 })
    .Projection(new long[] { 1, 2 })
    .Limit(100)
    .ExecuteAsync();
Console.WriteLine($"rows: {rows.Count}");

long n = await db.CountAsync("orders");
Console.WriteLine($"count: {n}"); // 2

// Run SQL.
await db.SqlAsync("UPDATE orders SET amount = 200.0 WHERE customer = 'Bob'");
```

## Schema constraints

`CreateTableAsync` forwards every column-spec key the caller puts in the
`Dictionary` to the daemon's `/kit/create_table` endpoint. The engine
recognises `enum_variants` (required for `ty: "enum"`), scalar `default_value`
(string, number, boolean, or null), dynamic `default_expr` (`"now"` or
`"uuid"`), top-level `constraints` block (unique / foreign-key / check).

```csharp
await db.CreateTableAsync("orders", new[]
{
    new Dictionary<string, object?>
    {
        ["id"] = 1L, ["name"] = "id",   ["ty"] = "int64",
        ["primary_key"] = true,  ["nullable"] = false,
    },
    // Enum column: value must be one of the three strings.
    new Dictionary<string, object?>
    {
        ["id"] = 2L, ["name"] = "status", ["ty"] = "enum",
        ["primary_key"] = false, ["nullable"] = false,
        ["enum_variants"] = new[] { "draft", "active", "archived" },
        ["default_value"] = "draft",
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
    // Timestamp column: engine fills "now" when the cell is omitted.
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

`enum_variants` arrives at the engine as a JSON array of strings, in
order; `default_value` preserves its JSON scalar type. The current
`CreateTableAsync(string, IEnumerable<Dictionary<string, object?>>)`
signature forwards both keys verbatim, so no client-side rename is needed.

### CHECK constraints (regex, range, equality)

CHECK constraints - including regex, range, equality, and boolean
composition - live in a top-level `constraints` block on the same
`/kit/create_table` payload:

```json
{
  "name": "users",
  "columns": [
    { "id": 1, "name": "id",    "ty": "int64",   "primary_key": true,  "nullable": false },
    { "id": 2, "name": "email", "ty": "varchar" }
  ],
  "constraints": {
    "checks": [
      {
        "id": 1,
        "name": "email_format",
        "expr": { "Regex": { "col": 2, "pattern": "^[^@]+@[^@]+$", "negated": false, "case_insensitive": true } }
      }
    ]
  }
}
```

Pass this object as the third `CreateTableAsync` argument. Existing calls that
pass a `CancellationToken` as argument three remain unchanged.

## Authentication

```csharp
// Bearer token (--auth-token mode)
using var db = new MongrelDBClient("http://127.0.0.1:8453", token: "my-secret-token", username: null, password: null);

// HTTP Basic (--auth-users mode)
using var db = new MongrelDBClient("http://127.0.0.1:8453", token: null, username: "admin", password: "s3cret");

// Default URL (http://127.0.0.1:8453) when baseUrl is null/empty
using var db = new MongrelDBClient(baseUrl: null);

// Custom HttpClient (timeouts, transport, sockets handler, etc.)
var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
using var db = new MongrelDBClient("http://127.0.0.1:8453", token: null, username: null, password: null, http: http);
```

A bearer token takes precedence over basic-auth credentials when both are supplied. When you pass your own `HttpClient`, the client does not dispose it - you retain ownership.

## Batch transactions

Operations are staged locally and committed atomically. The engine enforces
unique, foreign-key, and check constraints at commit time.

```csharp
var txn = db.BeginTransaction();
txn.Put("orders", Cells.Of(1, 10L, 2, "Dave", 3, 50.00), returning: false);
txn.Put("orders", Cells.Of(1, 11L, 2, "Eve",  3, 75.00), returning: false);
txn.DeleteByPk("orders", 2L);

try
{
    List<Dictionary<string, object?>> results = await txn.CommitAsync(); // atomic - all or nothing
}
catch (ConflictException e)
{
    // A constraint violation rolled back every op.
    Console.WriteLine($"duplicate: {e.Code} at op {e.OpIndex}");
    txn.Rollback(); // discard locally as well
}

// Idempotent commit - safe to retry; the daemon returns the original response.
var txn2 = db.BeginTransaction();
txn2.Put("orders", Cells.Of(1, 20L, 2, "Frank", 3, 100.00), returning: false);
await txn2.CommitAsync(idempotencyKey: "order-20-create");
```

A `Transaction` is single-use: calling `CommitAsync` or `Rollback` twice throws
`InvalidOperationException`. Create a fresh one with `db.BeginTransaction()` for
each batch.

## Native query builder

Conditions push down to the engine's specialized indexes. The builder accepts
friendly aliases that are translated to the server's on-wire keys: `column`
(→ `column_id`), `min`/`max` (→ `lo`/`hi`). The canonical keys are also
accepted directly.

```csharp
// Bitmap equality (low-cardinality columns).
await db.Query("orders")
    .Where("bitmap_eq", new Dictionary<string, object?> { ["column"] = 2L, ["value"] = "Alice" })
    .ExecuteAsync();

// Range query (learned-range index).
await db.Query("orders")
    .Where("range", new Dictionary<string, object?> { ["column"] = 3L, ["min"] = 50.0, ["max"] = 150.0 })
    .Limit(100).ExecuteAsync();

// Full-text search (FM-index).
await db.Query("documents")
    .Where("fm_contains", new Dictionary<string, object?> { ["column"] = 2L, ["pattern"] = "database performance" })
    .Limit(10).ExecuteAsync();

// Vector similarity search (HNSW).
await db.Query("embeddings")
    .Where("ann", new Dictionary<string, object?> { ["column"] = 2L, ["query"] = new double[] { 0.1, 0.2, 0.3 }, ["k"] = 10 })
    .ExecuteAsync();

// Check whether a result was capped by the limit.
QueryBuilder q = db.Query("orders")
    .Where("range", new Dictionary<string, object?> { ["column"] = 3L, ["min"] = 0L })
    .Limit(100);
List<Dictionary<string, object?>> rows = await q.ExecuteAsync();
if (q.Truncated)
{
    // result set hit the limit; more matches exist on the server
}
```

## SQL

```csharp
await db.SqlAsync("INSERT INTO orders (id, customer, amount) VALUES (99, 'Zoe', 999.0)");
await db.SqlAsync("CREATE TABLE archive AS SELECT * FROM orders WHERE amount > 500");

// Recursive CTEs and window functions
await db.SqlAsync("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r WHERE n<10) SELECT n FROM r");
await db.SqlAsync("SELECT id, ROW_NUMBER() OVER (PARTITION BY customer ORDER BY amount DESC) FROM orders");
```

The `/sql` endpoint generally streams Arrow IPC bytes for `SELECT`s; `SqlAsync()`
decodes JSON row sets when the daemon returns them and returns an empty list
otherwise (DDL/DML or binary bodies).

## User & role management

User, role, and permission management is performed through SQL against the
daemon's catalog. Passwords are Argon2id-hashed server-side.

```csharp
await db.SqlAsync("CREATE USER admin WITH PASSWORD 's3cret-pw'");
await db.SqlAsync("ALTER USER admin SET ADMIN TRUE");

await db.SqlAsync("CREATE ROLE analyst");
await db.SqlAsync("GRANT select ON orders TO analyst"); // table-level permission
await db.SqlAsync("GRANT analyst TO alice");

await db.SqlAsync("SELECT username FROM catalog.users"); // list users
await db.SqlAsync("SELECT name FROM catalog.roles");     // list roles
```

## History retention

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

## Error handling

Every non-2xx response is mapped to a typed exception. Catch the specific
subclass for the category, or catch `MongrelDBException` to handle any failure.
Each carries the HTTP status code and the server's decoded error envelope
(`Code`, `OpIndex`).

```csharp
try
{
    await db.GetSchemaForAsync("missing_table");
}
catch (NotFoundException e)
{
    Console.WriteLine($"not found: {e.Message}");
}
catch (AuthException e)
{
    Console.WriteLine($"not authorized: {e.Message}");
}
catch (ConflictException e)
{
    Console.WriteLine($"constraint {e.Code} at op {e.OpIndex}");
}
catch (QueryException e)
{
    Console.WriteLine($"query/server error: {e.Message} (status {e.Status})");
}

// Or inspect directly on the base type:
try
{
    await db.GetSchemaForAsync("missing_table");
}
catch (MongrelDBException e)
{
    Console.WriteLine($"status={e.Status} code={e.Code} msg={e.Message}");
    // e.g. status=404 code=NOT_FOUND msg=no such table
}
```

All public methods honor a `CancellationToken` so callers can cancel in-flight
requests cooperatively.

## API reference

### `MongrelDBClient`

| Method | Description |
|--------|-------------|
| `new MongrelDBClient(baseUrl)` | Construct a client (baseUrl defaults to `http://127.0.0.1:8453`) |
| `new MongrelDBClient(baseUrl, token, user, pass)` | With Bearer token or Basic auth |
| `new MongrelDBClient(baseUrl, token, user, pass, http)` | With a custom `HttpClient` |
| `HealthAsync()` | Check daemon health (returns `false` rather than throwing when unreachable) |
| `GetTableNamesAsync()` | List table names |
| `CreateTableAsync(name, columns, constraints?, indexes?)` | Create a table with optional constraints and all index definitions |
| `DropTableAsync(name)` | Drop a table |
| `CountAsync(table)` | Row count |
| `PutAsync(table, cells, key?)` | Insert a row |
| `UpsertAsync(table, cells, updateCells?, key?)` | Upsert a row |
| `DeleteAsync(table, rowId)` | Delete by row id |
| `DeleteByPkAsync(table, pk)` | Delete by primary key |
| `Query(table)` | Start a native query |
| `SqlAsync(sql)` | Execute SQL |
| `GetSchemaAsync()` | Full schema catalog |
| `GetSchemaForAsync(table)` | Single-table descriptor |
| `CompactAsync()` | Compact all tables |
| `CompactTableAsync(table)` | Compact one table |
| `GetHistoryRetentionAsync()` | Read the retained epoch window |
| `HistoryRetentionEpochsAsync()` | Read `history_retention_epochs` as `ulong` |
| `EarliestRetainedEpochAsync()` | Read `earliest_retained_epoch` as `ulong` |
| `SetHistoryRetentionEpochsAsync(epochs)` | Set the retained epoch window (requires admin) |
| `BeginTransaction()` | Start a batch |

### `QueryBuilder`

| Method | Description |
|--------|-------------|
| `Where(type, params)` | Add a native condition (AND-ed) |
| `Projection(columnIDs)` | Set column projection |
| `Limit(limit)` | Set row limit |
| `Offset(offset)` | Skip matching rows before the limit |
| `Build()` | Build the request payload |
| `ExecuteAsync()` | Run the query |
| `Truncated` | Whether the last `ExecuteAsync` result hit the limit |

### `Transaction`

| Method | Description |
|--------|-------------|
| `Put(table, cells, returning)` | Stage an insert |
| `Upsert(table, cells, updateCells, returning)` | Stage an upsert |
| `Delete(table, rowId)` | Stage a delete by row id |
| `DeleteByPk(table, pk)` | Stage a delete by primary key |
| `Count` | Number of staged operations |
| `CommitAsync(key?)` | Commit atomically |
| `Rollback()` | Discard all operations |

### Exceptions

| Exception | HTTP status | Meaning |
|-----------|-------------|---------|
| `MongrelDBException` | any | Base class for all client errors |
| `AuthException` | 401, 403 | Bad or missing credentials |
| `NotFoundException` | 404 | Missing table, schema, or resource |
| `ConflictException` | 409 | Unique, FK, check, or trigger violation (carries `Code` + `OpIndex`) |
| `QueryException` | 400, 5xx | Malformed query, server error, or transport failure |

All exceptions extend `MongrelDBException` and expose `Status`, `Code`, and `OpIndex`.

## Building and testing

The test suite is a live integration suite: it boots a real `mongreldb-server`
daemon and exercises the full client surface against it. Live tests use
`[SkippableFact]` and skip automatically when no daemon is available; the
offline tests (health-when-unreachable, auth header) always run.

```sh
# Build and run the offline checks:
dotnet build
dotnet test

# Run the live suite. The harness boots mongreldb-server itself if it can find
# the binary (in this order):
#   1. the MONGRELDB_SERVER env var (path to the server binary)
#   2. ./bin/mongreldb-server
#   3. mongreldb-server on PATH
# Or point it at an already-running daemon with MONGRELDB_URL.
MONGRELDB_SERVER=./bin/mongreldb-server dotnet test
```

Fetch a prebuilt server binary from the [MongrelDB releases](https://github.com/visorcraft/MongrelDB/releases):

```sh
mkdir -p bin
curl -fsSL -o bin/mongreldb-server \
  https://github.com/visorcraft/MongrelDB/releases/download/v0.61.1/mongreldb-server-linux-x64
chmod +x bin/mongreldb-server
```

## Native embedding (Tier 1)

For in-process access with zero serialization overhead, install the
**`Visorcraft.MongrelDB.Native`** package alongside the HTTP client. It
bundles prebuilt `libmongreldb` + `libmongreldb_kit` under
`runtimes/<rid>/native/` and exposes the engine via P/Invoke:

```sh
dotnet add package Visorcraft.MongrelDB.Native
```

```csharp
using Visorcraft.MongrelDB.Native;

// Create an embedded database (no daemon needed).
using var db = MongrelDBNative.Create("/path/to/dbdir", schemaJson);

// SQL, migrations, and the query builder all work in-process.
db.SqlRows("INSERT INTO users (id, name) VALUES (1, 'alice')");
var rows = db.SqlRows("SELECT id, name FROM users");

// Arrow IPC for high-performance columnar reads.
byte[] arrow = db.SqlArrow("SELECT * FROM users");

// Migrations via the Kit runner.
db.Migrate(migrationsJson);

// Query builder.
var matches = db.QuerySelect(/* Select AST JSON */);
```

The native libraries are resolved in this order:
1. `MONGRELDB_NATIVE_DIR` env var (point at a directory with the `.so`/`.dylib`/`.dll`)
2. `runtimes/<rid>/native/` alongside the application (NuGet auto-layout)
3. System search path (`LD_LIBRARY_PATH`, `DYLD_LIBRARY_PATH`, `PATH`)

The HTTP client (`Visorcraft.MongrelDB`) stays dependency-free. Use the
native package when you want the "better-sqlite3" embedded experience, or
the HTTP client when you need to connect to a shared daemon.

## Contributing

Contributions are welcome. Please:

1. Open an issue first for non-trivial changes.
2. Add focused tests near your change - the suite must stay green.
3. Keep the library dependency-free (.NET base class library only).

## License

Dual-licensed under the **MIT License** or the **Apache License, Version 2.0**,
at your option. See [MIT](LICENSE-MIT) OR [Apache-2.0](LICENSE-APACHE) for the full text.

`SPDX-License-Identifier: MIT OR Apache-2.0`
