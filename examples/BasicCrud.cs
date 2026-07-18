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
// Unique name per run so re-running the example never collides with a
// leftover table from a previous (possibly failed) run.
string table = "example_crud_" + Guid.NewGuid().ToString("N");

using var db = new MongrelDBClient(Url);

// Track whether the table exists so the finally below only drops it if it was
// actually created (otherwise a failed health check would raise a NotFound).
bool created = false;
try
{
// 1. Health check; bail out if the daemon is unreachable.
if (!await db.HealthAsync())
{
    Console.Error.WriteLine($"daemon not reachable at {Url}");
    return 1;
}
Console.WriteLine("Connected to MongrelDB");

// 2. Create the table. Keep the schema to core scalar types so the example
//    stays portable across engine minor releases (timestamp/enum defaults
//    have moved between 0.55–0.59 wire shapes).
var columns = new[]
{
    Column(1, "id", "int64", primaryKey: true),
    Column(2, "name", "varchar", primaryKey: false),
    Column(3, "score", "float64", primaryKey: false),
};
var constraints = new Dictionary<string, object?>
{
    ["checks"] = new object[]
    {
        new Dictionary<string, object?>
        {
            ["id"] = 1,
            ["name"] = "score_nonneg",
            ["expr"] = new Dictionary<string, object?>
            {
                ["Ge"] = new object[]
                {
                    new Dictionary<string, object?> { ["Col"] = 3 },
                    new Dictionary<string, object?> { ["Lit"] = new Dictionary<string, object?> { ["Float64"] = 0.0 } },
                },
            },
        },
    },
};
long tableId = await db.CreateTableAsync(table, columns, constraints);
created = true;
Console.WriteLine($"Created table {table} (id {tableId})");

// 3. Insert three rows. Cells.Of pairs column ids with values.
await db.PutAsync(table, Cells.Of(1, 1L, 2, "Alice", 3, 95.5));
await db.PutAsync(table, Cells.Of(1, 2L, 2, "Bob", 3, 82.0));
await db.PutAsync(table, Cells.Of(1, 3L, 2, "Carol", 3, 78.3));
Console.WriteLine("Inserted 3 rows");

Console.WriteLine($"Total rows: {await db.CountAsync(table)}");

// 4. Query all rows (no conditions).
List<Dictionary<string, object?>> all = await db.Query(table).ExecuteAsync();
Console.WriteLine($"Query returned {all.Count} rows:");
foreach (var row in all)
{
    Console.WriteLine($"  {string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value}"))}");
}

// 5. Upsert (update) Alice's score. updateCells supplies the values written on
//    a primary-key conflict.
await db.UpsertAsync(table,
    Cells.Of(1, 1L, 2, "Alice", 3, 100.0),
    updateCells: Cells.Of(2, "Alice", 3, 100.0));
Console.WriteLine("Upserted Alice's score to 100.0");
Console.WriteLine($"Total rows after upsert: {await db.CountAsync(table)}");

// 6. Delete Carol (primary key 3).
await db.DeleteByPkAsync(table, 3L);
Console.WriteLine($"Deleted Carol; remaining rows: {await db.CountAsync(table)}");
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
