// Example: query builder conditions with the MongrelDB .NET client.
//
// Drop this file in as Program.cs of a console project referencing
// Visorcraft.MongrelDB and run with `dotnet run`. Requires a mongreldb-server
// daemon running on http://127.0.0.1:8453.
//
// Creates a table, inserts five rows with varying scores, then uses the native
// query builder to fetch rows by a range condition and by an exact primary-key
// match. Cleans up by dropping the table.

using Visorcraft.MongrelDB;

const string Url = "http://127.0.0.1:8453";
const string Table = "example_query";

using var db = new MongrelDBClient(Url);

if (!await db.HealthAsync())
{
    Console.Error.WriteLine($"daemon not reachable at {Url}");
    return 1;
}
Console.WriteLine("Connected to MongrelDB");

await db.CreateTableAsync(Table, new[]
{
    Column(1, "id", "int64", primaryKey: true),
    Column(2, "name", "varchar", primaryKey: false),
    Column(3, "score", "float64", primaryKey: false),
});
Console.WriteLine($"Created table {Table}");

// Five rows with varying scores.
await db.PutAsync(Table, Cells.Of(1, 1L, 2, "Alice", 3, 40.0));
await db.PutAsync(Table, Cells.Of(1, 2L, 2, "Bob", 3, 65.0));
await db.PutAsync(Table, Cells.Of(1, 3L, 2, "Carol", 3, 82.0));
await db.PutAsync(Table, Cells.Of(1, 4L, 2, "Dave", 3, 91.0));
await db.PutAsync(Table, Cells.Of(1, 5L, 2, "Eve", 3, 12.5));
Console.WriteLine("Inserted 5 rows");

// Range condition: scores in [60.0, 90.0]. "column" maps to column_id, so pass
// the numeric column id (3L), not the name.
List<Dictionary<string, object?>> rng = await db.Query(Table)
    .Where("range", new Dictionary<string, object?> { ["column"] = 3L, ["min"] = 60.0, ["max"] = 90.0 })
    .ExecuteAsync();
Console.WriteLine($"Range query (score in [60,90]) returned {rng.Count} rows:");
foreach (var row in rng)
{
    Console.WriteLine($"  {string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value}"))}");
}

// Primary-key condition: fetch the single row with id == 4.
List<Dictionary<string, object?>> pk = await db.Query(Table)
    .Where("pk", new Dictionary<string, object?> { ["value"] = 4L })
    .ExecuteAsync();
Console.WriteLine($"PK query (id == 4) returned {pk.Count} rows:");
foreach (var row in pk)
{
    Console.WriteLine($"  {string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value}"))}");
}

await db.DropTableAsync(Table);
Console.WriteLine($"Dropped table {Table}");
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
