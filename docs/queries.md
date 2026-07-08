# Queries

The fluent `QueryBuilder` pushes conditions down to MongrelDB's native indexes
for sub-millisecond lookups — bitmap, learned-range, FM-index full text, HNSW
vector similarity, and more. Each condition type maps to one specialized
index; conditions are AND-ed together.

```csharp
var q = db.Query("orders")
    .Where("range", new Dictionary<string, object?> { ["column"] = 3L, ["min"] = 100.0, ["max"] = 500.0 })
    .Projection(new long[] { 1, 2 })
    .Limit(100);
List<Dictionary<string, object?>> rows = await q.ExecuteAsync();
```

This guide covers every condition type, projection, limits and truncation,
combining conditions, and the friendly aliases the builder translates for you.

---

## The basics

Every query starts with `MongrelDBClient.Query(table)` and ends with
`ExecuteAsync`:

| Method / property | Purpose |
|-------------------|---------|
| `Where(condType, params)` | Add a native condition. Multiple `Where` calls are AND-ed. |
| `Projection(columnIDs)` | Return only these column ids (`null` means all columns). |
| `Limit(n)` | Cap the number of rows. |
| `Build()` | Produce the request payload (useful for debugging). |
| `ExecuteAsync(ct)` | Send and decode. Records the `Truncated` flag. |
| `Truncated` | Whether the last `ExecuteAsync` hit the limit. |

The request body produced by `Build()` matches the daemon's `/kit/query`
shape:

```json
{
  "table": "orders",
  "conditions": [{"range": {"column_id": 3, "lo": 100.0, "hi": 500.0}}],
  "projection": [1, 2],
  "limit": 100
}
```

## Condition types

`params` is an `IDictionary<string, object?>`. Column references use the
numeric **column id** (`long`), never the column name. Always suffix integer
literals with `L` so they box as `long`.

### `pk` — exact primary-key match

The fastest lookup. `value` is the primary-key value.

```csharp
await db.Query("orders")
    .Where("pk", new Dictionary<string, object?> { ["value"] = 42L })
    .ExecuteAsync();
```

### `range` — integer range (learned-range index)

Inclusive bounds. Omit `lo` or `hi` for an open range.

```csharp
await db.Query("orders")
    .Where("range", new Dictionary<string, object?>
    {
        ["column"] = 3L,   // column id
        ["min"] = 100L,
        ["max"] = 500L,
    })
    .ExecuteAsync();

// Open-ended: amount >= 100
await db.Query("orders")
    .Where("range", new Dictionary<string, object?> { ["column"] = 3L, ["min"] = 100L })
    .ExecuteAsync();
```

### `range_f64` — float range with inclusive/exclusive control

Adds `lo_inclusive` / `hi_inclusive` flags (default inclusive).

```csharp
await db.Query("orders")
    .Where("range_f64", new Dictionary<string, object?>
    {
        ["column"] = 3L,
        ["min"] = 100.0,
        ["max"] = 500.0,
        ["min_inclusive"] = true,
        ["max_inclusive"] = false, // (100.0, 500.0]
    })
    .ExecuteAsync();
```

### `bitmap_eq` — equality on a bitmap-indexed column

Best for low-cardinality columns (status, category, booleans).

```csharp
await db.Query("orders")
    .Where("bitmap_eq", new Dictionary<string, object?> { ["column"] = 2L, ["value"] = "Alice" })
    .ExecuteAsync();
```

### `bitmap_in` — IN predicate on a bitmap-indexed column

Match any of a set of values.

```csharp
await db.Query("orders")
    .Where("bitmap_in", new Dictionary<string, object?>
    {
        ["column"] = 2L,
        ["values"] = new List<object?> { "Alice", "Bob", "Carol" },
    })
    .ExecuteAsync();
```

### `is_null` / `is_not_null` — null checks

```csharp
await db.Query("orders").Where("is_null", new Dictionary<string, object?> { ["column"] = 3L }).ExecuteAsync();
await db.Query("orders").Where("is_not_null", new Dictionary<string, object?> { ["column"] = 3L }).ExecuteAsync();
```

### `fm_contains` — full-text substring search (FM-index)

Substring match within a column. Use `pattern` (the server key) or the
friendly `value` alias — both translate to `pattern` on the wire for FTS
conditions.

```csharp
await db.Query("documents")
    .Where("fm_contains", new Dictionary<string, object?>
    {
        ["column"] = 2L,
        ["pattern"] = "database performance",
    })
    .Limit(10)
    .ExecuteAsync();

// Friendly alias: "value" -> "pattern" for fm_contains only.
await db.Query("documents")
    .Where("fm_contains", new Dictionary<string, object?> { ["column"] = 2L, ["value"] = "database" })
    .ExecuteAsync();
```

### `fm_contains_all` — multiple substrings, all must match

```csharp
await db.Query("documents")
    .Where("fm_contains_all", new Dictionary<string, object?>
    {
        ["column"] = 2L,
        ["patterns"] = new List<object?> { "database", "performance" },
    })
    .ExecuteAsync();
```

### `ann` — dense vector similarity (HNSW)

Approximate nearest-neighbors over a vector column. `k` is the result count.
Pass the query vector as `float[]` or `List<object?>`.

```csharp
await db.Query("embeddings")
    .Where("ann", new Dictionary<string, object?>
    {
        ["column"] = 2L,
        ["query"] = new float[] { 0.1f, 0.2f, 0.3f, 0.4f },
        ["k"] = 10L,
    })
    .ExecuteAsync();
```

### `sparse_match` — sparse vector match

For sparse/bag-of-words vectors.

```csharp
await db.Query("docs")
    .Where("sparse_match", new Dictionary<string, object?>
    {
        ["column"] = 2L,
        ["query"] = new Dictionary<long, object?> { [0] = 1.0, [7] = 0.5, [42] = 2.0 },
        ["k"] = 10L,
    })
    .ExecuteAsync();
```

### `min_hash_similar` — MinHash similarity

Near-duplicate detection via MinHash signatures.

```csharp
await db.Query("pages")
    .Where("min_hash_similar", new Dictionary<string, object?>
    {
        ["column"] = 2L,
        ["query"] = new long[] { 12, 99, 421, 7 },
        ["k"] = 5L,
    })
    .ExecuteAsync();
```

## Projection (column selection)

`Projection(new long[] { ... })` restricts the columns in each returned row.
Pass `null` (or skip the call) for all columns. Projecting to only the columns
you need cuts bandwidth and decode cost.

```csharp
// Return only the id and customer columns.
await db.Query("orders")
    .Where("range", new Dictionary<string, object?> { ["column"] = 3L, ["min"] = 100L })
    .Projection(new long[] { 1, 2 })
    .ExecuteAsync();
```

Returned rows are `Dictionary<string, object?>` keyed by the column id as a
JSON-decoded key (a string like `"2"`). Cast accordingly:

```csharp
var rows = await db.Query("orders").Projection(new long[] { 1L, 2L }).ExecuteAsync();
foreach (var r in rows)
{
    string? customer = r["2"] as string;
    Console.WriteLine(customer);
}
```

## Limit and the truncated flag

`Limit(n)` caps the result. When the server has more matches than the limit
allows, it returns the first `n` and sets `truncated: true`. Read the
`Truncated` property **after** `ExecuteAsync`.

```csharp
var q = db.Query("orders")
    .Where("range", new Dictionary<string, object?> { ["column"] = 3L, ["min"] = 0L })
    .Limit(100);
var rows = await q.ExecuteAsync();
if (q.Truncated)
{
    // 100 rows came back but more exist on the server. Either raise the
    // limit, page with a range predicate on the PK, or accept the cap.
    Console.WriteLine($"result capped at {rows.Count}; more rows available");
}
```

`Truncated` returns `false` until `ExecuteAsync` has run, so build a fresh
query for each independent lookup.

## Multiple AND conditions

Chain `Where` calls. Every condition must match; the server intersects the
index results.

```csharp
// Customer is Alice AND amount is between 100 and 500.
await db.Query("orders")
    .Where("bitmap_eq", new Dictionary<string, object?> { ["column"] = 2L, ["value"] = "Alice" })
    .Where("range", new Dictionary<string, object?> { ["column"] = 3L, ["min"] = 100L, ["max"] = 500L })
    .Projection(new long[] { 1, 3 })
    .Limit(50)
    .ExecuteAsync();
```

Because each `Where` targets a different specialized index, the engine can
pick the most selective one to drive the lookup and intersect the rest.

## Friendly alias translation

The builder accepts readable parameter names and translates them to the
server's canonical on-wire keys. Both spellings work, so use whichever is
clearer in context.

| You write | Sent as | Applies to |
|-----------|---------|------------|
| `column` | `column_id` | all condition types |
| `min` | `lo` | `range`, `range_f64` |
| `max` | `hi` | `range`, `range_f64` |
| `min_inclusive` | `lo_inclusive` | `range_f64` |
| `max_inclusive` | `hi_inclusive` | `range_f64` |
| `value` | `pattern` | `fm_contains`, `fm_contains_all` only |

The `value` → `pattern` alias applies **only** to FTS conditions, because
`pk` and `bitmap_eq` use `value` as their canonical key. For those, write
`value` directly.

```csharp
// pk: "value" stays "value" (canonical)
.Where("pk", new Dictionary<string, object?> { ["value"] = 42L })

// fm_contains: "value" is translated to "pattern"
.Where("fm_contains", new Dictionary<string, object?> { ["column"] = 2L, ["value"] = "search term" })
// equivalent to:
.Where("fm_contains", new Dictionary<string, object?> { ["column_id"] = 2L, ["pattern"] = "search term" })
```

## Putting it together

A realistic combined lookup — bitmap equality + range + projection + limit +
truncation check:

```csharp
public async Task<List<Dictionary<string, object?>>> TopSpendersAsync(
    string customer, CancellationToken ct = default)
{
    var q = db.Query("orders")
        .Where("bitmap_eq", new Dictionary<string, object?> { ["column"] = 2L, ["value"] = customer })
        .Where("range", new Dictionary<string, object?> { ["column"] = 3L, ["min"] = 100L })
        .Projection(new long[] { 1, 3 })
        .Limit(50);
    var rows = await q.ExecuteAsync(ct);
    if (q.Truncated)
    {
        Console.Error.WriteLine("warning: TopSpenders result capped at 50");
    }
    return rows;
}
```

For arbitrary predicates, joins, and aggregations that the native indexes do
not cover, use SQL instead — see [sql.md](sql.md).
