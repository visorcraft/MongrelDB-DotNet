// Example: atomic batch transactions with the MongrelDB .NET client.
//
// Drop this file in as Program.cs of a console project referencing
// Visorcraft.MongrelDB and run with `dotnet run`. Requires a mongreldb-server
// daemon running on http://127.0.0.1:8453.
//
// Creates a table, stages three inserts in a single transaction, commits them
// atomically, verifies the count, then demonstrates idempotent retries by
// re-committing with the same idempotency key (the daemon returns the original
// result and applies no duplicate rows). Cleans up by dropping the table.

using Visorcraft.MongrelDB;

const string Url = "http://127.0.0.1:8453";
// Unique name per run so re-running the example never collides with a
// leftover table from a previous (possibly failed) run.
string table = "example_txn_" + Guid.NewGuid().ToString("N");
// Idempotency key unique per run, reused for both commits below so the
// duplicate commit replays the original result (no double-apply).
string key = "example-txn-" + Guid.NewGuid().ToString("N");

using var db = new MongrelDBClient(Url);

// Track whether the table exists so the finally below only drops it if it was
// actually created (otherwise a failed health check would raise a NotFound).
bool created = false;
try
{
if (!await db.HealthAsync())
{
    Console.Error.WriteLine($"daemon not reachable at {Url}");
    return 1;
}
Console.WriteLine("Connected to MongrelDB");

await db.CreateTableAsync(table, new[]
{
    Column(1, "id", "int64", primaryKey: true),
    Column(2, "name", "varchar", primaryKey: false),
    Column(3, "score", "float64", primaryKey: false),
});
created = true;
Console.WriteLine($"Created table {table}");

// Stage three puts and commit them atomically. Either every op lands or none
// do; a constraint violation rolls back the whole batch.
var txn = db.BeginTransaction();
txn.Put(table, Cells.Of(1, 1L, 2, "Alice", 3, 95.5), returning: false);
txn.Put(table, Cells.Of(1, 2L, 2, "Bob", 3, 82.0), returning: false);
txn.Put(table, Cells.Of(1, 3L, 2, "Carol", 3, 78.3), returning: false);
Console.WriteLine($"Staged {txn.Count} operations");

List<Dictionary<string, object?>> results = await txn.CommitAsync();
Console.WriteLine($"Committed atomically: {results.Count} operations applied");

Console.WriteLine($"Verified row count after commit: {await db.CountAsync(table)}");

// Idempotent retry: stage the same batch again with an idempotency key, then
// commit a second time with the SAME key. The daemon replays the original
// result and applies no extra rows.
var retry = db.BeginTransaction();
retry.Put(table, Cells.Of(1, 4L, 2, "Dave", 3, 60.0), returning: false);
await retry.CommitAsync(key);
Console.WriteLine($"After first idempotent commit: {await db.CountAsync(table)} rows");

var retry2 = db.BeginTransaction();
retry2.Put(table, Cells.Of(1, 4L, 2, "Dave", 3, 60.0), returning: false);
await retry2.CommitAsync(key);
Console.WriteLine($"After duplicate idempotent commit (same key): {await db.CountAsync(table)} rows (no double-apply)");
}
finally
{
    // Always clean up, even if something above threw.
    if (created)
    {
        await db.DropTableAsync(table);
        Console.WriteLine($"Dropped table {table}");
    }
}
return 0;

static Dictionary<string, object?> Column(long id, string name, string ty, bool primaryKey) =>
    new()
    {
        ["id"] = id,
        ["name"] = name,
        ["ty"] = ty,
        ["primary_key"] = primaryKey,
        ["nullable"] = false,
    };
