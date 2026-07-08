# SQL

MongrelDB ships a DataFusion-backed SQL engine at `POST /sql`. From C#, run
SQL with `MongrelDBClient.SqlAsync`:

```csharp
List<Dictionary<string, object?>> rows = await db.SqlAsync("SELECT 1");
```

This guide covers the SQL surface - DDL, DML, `CREATE TABLE AS SELECT`,
recursive CTEs, and window functions - and when to reach for SQL versus the
native query builder.

---

## How `SqlAsync` behaves

`MongrelDBClient.SqlAsync(sql, ct)` sends `{"sql": "..."}` to `/sql`. It
returns the decoded rows when the daemon replies with a JSON result set, and
an empty list otherwise.

In practice:

- **DDL and DML** (`CREATE TABLE`, `INSERT`, `UPDATE`, `DELETE`) reply with a
  non-JSON status body. `SqlAsync` returns an empty list - success is the
  signal.
- **`SELECT`** in most daemon builds streams Arrow IPC bytes rather than JSON.
  `SqlAsync` therefore returns an empty list for SELECTs too. Use the native
  `QueryBuilder` for typed row retrieval in application code, and use
  `SqlAsync` for statements whose execution is the goal (DDL/DML/admin).

Errors are mapped to the same typed exceptions as everything else: an HTTP 400
or 5xx raises `QueryException`; 409 raises `ConflictException`; and so on. See
[errors.md](errors.md).

```csharp
try
{
    await db.SqlAsync("INSERT INTO orders (id, customer, amount) VALUES (99, 'Zoe', 999.0)");
}
catch (ConflictException e) when (e.Code == "UNIQUE_VIOLATION")
{
    Console.WriteLine($"duplicate row: {e.Message}");
}
```

## CREATE TABLE

Define a table in SQL instead of via `CreateTableAsync`. Column ids are
assigned by the server when not stated.

```csharp
await db.SqlAsync("""
        CREATE TABLE products (
          id          INT64 PRIMARY KEY,
          name        VARCHAR,
          price       FLOAT64,
          category    VARCHAR,
          in_stock    BOOLEAN
        )
        """);
```

## INSERT

```csharp
await db.SqlAsync("INSERT INTO products (id, name, price, category, in_stock) VALUES (1, 'Widget', 9.99, 'tools', true)");
await db.SqlAsync("INSERT INTO products VALUES (2, 'Gadget', 19.99, 'tools', true)");
```

For bulk inserts, the native batch transaction (`BeginTransaction`) is usually
faster because it stages ops in one round trip without re-parsing SQL.

## UPDATE

```csharp
await db.SqlAsync("UPDATE products SET price = 14.99 WHERE id = 1");
await db.SqlAsync("UPDATE orders SET amount = 200.0 WHERE customer = 'Bob'");
```

## DELETE

```csharp
await db.SqlAsync("DELETE FROM products WHERE in_stock = false");
await db.SqlAsync("DELETE FROM products WHERE id = 2");
```

## SELECT

```csharp
await db.SqlAsync("SELECT id, name FROM products WHERE category = 'tools' ORDER BY price");
await db.SqlAsync("SELECT category, COUNT(*) AS n FROM products GROUP BY category");
```

Remember SELECT bodies usually arrive as Arrow IPC, so `SqlAsync` returns an
empty list. To read rows back into C# dictionaries, mirror the same lookup
with the `QueryBuilder`.

## CREATE TABLE AS SELECT

Materialize a query result into a new table. Great for snapshots, rollups,
and denormalized aggregates.

```csharp
// Snapshot all high-value orders into a new table.
await db.SqlAsync("CREATE TABLE archive AS SELECT * FROM orders WHERE amount > 500");

// Roll up sales by customer.
await db.SqlAsync("""
        CREATE TABLE sales_by_customer AS
        SELECT customer, SUM(amount) AS total
        FROM orders
        GROUP BY customer
        """);
```

The new table inherits column types from the query. Query it afterward with
the native builder or SQL.

## Recursive CTEs

`WITH RECURSIVE` is fully supported. Classic use cases: series generation,
hierarchy/graph traversal.

```csharp
// Generate the numbers 1..10.
await db.SqlAsync("""
        WITH RECURSIVE r(n) AS (
          SELECT 1
          UNION ALL
          SELECT n + 1 FROM r WHERE n < 10
        )
        SELECT n FROM r
        """);
```

A common practical example is walking an adjacency list:

```csharp
await db.SqlAsync("""
        WITH RECURSIVE descendants(id) AS (
          SELECT id FROM categories WHERE id = 1
          UNION ALL
          SELECT c.id FROM categories c
          JOIN descendants d ON c.parent_id = d.id
        )
        SELECT id FROM descendants
        """);
```

## Window functions

Window functions compute aggregates/rankings across a moving window without
collapsing rows. Useful for top-N-per-group, running totals, and row numbers.

```csharp
// Row number within each customer, ordered by amount descending.
await db.SqlAsync("""
        SELECT id, customer, amount,
               ROW_NUMBER() OVER (PARTITION BY customer ORDER BY amount DESC) AS rn
        FROM orders
        """);

// Running total per customer.
await db.SqlAsync("""
        SELECT id, customer, amount,
               SUM(amount) OVER (PARTITION BY customer ORDER BY id) AS running_total
        FROM orders
        """);
```

`RANK()`, `DENSE_RANK()`, `LAG()`, `LEAD()`, `NTILE()`, and the usual
window-frame clauses are available through DataFusion.

## When to use SQL vs. the query builder

Both read from the same tables, but they are optimized for different jobs.

| Reach for | When |
|-----------|------|
| **`QueryBuilder`** | Point lookups, range scans, bitmap filters, full-text, and vector similarity that map to a native index. Sub-millisecond, no parser overhead, and rows decode into C# dictionaries directly. |
| **SQL** | DDL (`CREATE TABLE`, schemas, materialized views), multi-statement setup, joins, recursive CTEs, window functions, and arbitrary aggregates. Also the natural choice for admin scripts and one-off analysis. |

Rules of thumb:

- Need a typed `List<Dictionary<string, object?>>` of matching rows? Use the
  query builder.
- Building/dropping tables, or running a `CREATE TABLE AS SELECT`? Use SQL.
- Joining multiple tables, computing rankings, or walking a graph? Use SQL.
- Filtering by one or more indexed columns? Use the query builder - it is
  faster and avoids Arrow-to-C# decoding.

Mix freely: create tables with SQL, write rows with `PutAsync`, read them
back with `QueryBuilder`, and run analytics with SQL.

## Next steps

- [queries.md](queries.md) - every native index condition in detail
- [transactions.md](transactions.md) - bulk inserts via batch transactions
- [errors.md](errors.md) - handling SQL execution errors
