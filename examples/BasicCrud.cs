// Example: basic CRUD operations with the MongrelDB .NET client.
//
// Build a small console project that references Visorcraft.MongrelDB, drop this
// file in as Program.cs, and run with `dotnet run`. Requires a mongreldb-server
// daemon running on http://127.0.0.1:8453.
//
// Creates a table, inserts three rows, counts them, queries all rows, upserts
// (updates) one row by primary key, deletes one row, then drops the table.
// Progress is printed at every step.

using Visorcraft.MongrelDB;

const string Url = "http://127.0.0.1:8453";
const string Table = "example_crud";

using var db = new MongrelDBClient(Url);

// 1. Health check; bail out if the daemon is unreachable.
if (!await db.HealthAsync())
{
    Console.Error.WriteLine($"daemon not reachable at {Url}");
    return 1;
}
Console.WriteLine("Connected to MongrelDB");

// 2. Create the table. Schema: id (int64 PK), name (varchar), score (float64).
long tableId = await db.CreateTableAsync(Table, new[]
{
    Column(1, "id", "int64", primaryKey: true),
    Column(2, "name", "varchar", primaryKey: false),
    Column(3, "score", "float64", primaryKey: false),
});
Console.WriteLine($"Created table {Table} (id {tableId})");

// 3. Insert three rows. Cells.Of pairs column ids with values.
await db.PutAsync(Table, Cells.Of(1, 1L, 2, "Alice", 3, 95.5));
await db.PutAsync(Table, Cells.Of(1, 2L, 2, "Bob", 3, 82.0));
await db.PutAsync(Table, Cells.Of(1, 3L, 2, "Carol", 3, 78.3));
Console.WriteLine("Inserted 3 rows");

Console.WriteLine($"Total rows: {await db.CountAsync(Table)}");

// 4. Query all rows (no conditions).
List<Dictionary<string, object?>> all = await db.Query(Table).ExecuteAsync();
Console.WriteLine($"Query returned {all.Count} rows:");
foreach (var row in all)
{
    Console.WriteLine($"  {string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value}"))}");
}

// 5. Upsert (update) Alice's score. updateCells supplies the values written on
//    a primary-key conflict.
await db.UpsertAsync(Table,
    Cells.Of(1, 1L, 2, "Alice", 3, 100.0),
    updateCells: Cells.Of(2, "Alice", 3, 100.0));
Console.WriteLine("Upserted Alice's score to 100.0");
Console.WriteLine($"Total rows after upsert: {await db.CountAsync(Table)}");

// 6. Delete Carol (primary key 3).
await db.DeleteByPkAsync(Table, 3L);
Console.WriteLine($"Deleted Carol; remaining rows: {await db.CountAsync(Table)}");

// 7. Cleanup.
await db.DropTableAsync(Table);
Console.WriteLine($"Dropped table {Table}");
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
