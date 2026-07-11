using Visorcraft.MongrelDB;
using Visorcraft.MongrelDB.Native;

// Example: basic CRUD with the native embedded MongrelDB engine (Tier 1).
//
// Unlike BasicCrud.cs which connects to a daemon over HTTP, this example runs
// the engine in-process via P/Invoke. No daemon needed.
//
// Run:
//   export MONGRELDB_NATIVE_DIR=/path/to/dir/with/libmongreldb.so
//   dotnet run --project examples/NativeBasicCrud.csproj

string dbDir = Path.Combine(Path.GetTempPath(), "mdb_native_example_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());

string schemaJson = """
    {
        "tables": [{
            "id": 1,
            "name": "users",
            "columns": [
                {"id":1,"name":"id","storage_type":"int64","application_type":"int64","nullable":false,"primary_key":true,"default":null,"generated":false},
                {"id":2,"name":"name","storage_type":"text","application_type":"text","nullable":true,"primary_key":false,"default":null,"generated":false},
                {"id":3,"name":"email","storage_type":"text","application_type":"text","nullable":true,"primary_key":false,"default":null,"generated":false}
            ],
            "primary_key": ["id"]
        }]
    }
    """;

Console.WriteLine("=== Native Embedded Basic CRUD ===");
Console.WriteLine($"Database dir: {dbDir}");
Console.WriteLine();

using var db = MongrelDBNative.Create(dbDir, schemaJson);
Console.WriteLine("1. Database created with schema (users table)");

// Insert rows via SQL.
db.SqlRows("INSERT INTO users (id, name, email) VALUES (1, 'Alice', 'alice@example.com')");
db.SqlRows("INSERT INTO users (id, name, email) VALUES (2, 'Bob', 'bob@example.com')");
db.SqlRows("INSERT INTO users (id, name, email) VALUES (3, 'Carol', 'carol@example.com')");
Console.WriteLine("2. Inserted 3 rows via SQL");

// SELECT via SQL (JSON rows).
var rows = db.SqlRows("SELECT id, name, email FROM users ORDER BY id");
Console.WriteLine("3. SELECT all rows:");
Console.WriteLine($"   {string.Join(", ", rows.Select(r => r["name"]))}");

// Arrow IPC for columnar reads.
byte[] arrow = db.SqlArrow("SELECT id FROM users");
Console.WriteLine($"4. Arrow IPC: {arrow.Length} bytes");

// Migration: add an orders table.
string migrations = """
    [{"version":1,"name":"add_orders","ops":[{"raw_sql":"CREATE TABLE orders (id INT64 PRIMARY KEY, user_id INT64, total FLOAT64)"}]}]
    """;
db.Migrate(migrations);
Console.WriteLine("5. Migration: created 'orders' table");

// Insert into the migrated table.
db.SqlRows("INSERT INTO orders (id, user_id, total) VALUES (1, 1, 99.99)");
db.SqlRows("INSERT INTO orders (id, user_id, total) VALUES (2, 2, 49.99)");

// SQL JOIN across both tables.
var joinRows = db.SqlRows("SELECT u.name, o.total FROM users u JOIN orders o ON u.id = o.user_id ORDER BY o.total DESC");
Console.WriteLine("6. SQL JOIN (users + orders):");
foreach (var row in joinRows)
    Console.WriteLine($"   {row["name"]}: {row["total"]}");

// Kit query builder: SELECT.
string selectJson = """
    {"table":"users","columns":[],"filter":null,"order_by":[],"limit":null,"offset":null}
    """;
var queryResult = db.QuerySelect(selectJson);
Console.WriteLine("7. Kit query builder SELECT:");
Console.WriteLine($"   {string.Join(", ", queryResult.Select(r => r["name"]))}");

// Read back applied migrations.
string applied = db.AppliedMigrationsJson();
Console.WriteLine($"8. Applied migrations: {applied}");

Console.WriteLine();
Console.WriteLine("=== All operations completed successfully! ===");
