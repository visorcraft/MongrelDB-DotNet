# Error handling

Every non-2xx response from the daemon is mapped to a typed C# exception. This
is the complete reference: the exception hierarchy, the properties carried on
each exception, the HTTP-status mapping, the daemon's error envelope, and
recovery patterns for each category.

---

## The exception hierarchy

All client errors extend `MongrelDBException` (which extends `Exception`).
Catch `MongrelDBException` to handle any failure, or catch one of the specific
subclasses:

```
Exception
└── MongrelDBException            (base; carries Status, Code, OpIndex)
    ├── AuthException             HTTP 401 / 403
    ├── NotFoundException         HTTP 404
    ├── ConflictException         HTTP 409
    └── QueryException            HTTP 400 / 5xx / everything else
```

| Exception | Meaning | Typical cause |
|-----------|---------|---------------|
| `MongrelDBException` | Base class; any client-side failure | Catch-all parent |
| `AuthException` | HTTP 401 or 403 | Missing/bad credentials against an auth-enabled daemon |
| `NotFoundException` | HTTP 404 | Missing table, schema, or other resource |
| `ConflictException` | HTTP 409 | Unique, foreign-key, check, or trigger violation at commit |
| `QueryException` | HTTP 400 or 5xx | Malformed request, transport failure, server error, JSON decode errors |

`QueryException` is also the type raised for client-side failures that do not
correspond to an HTTP response (e.g. an `HttpRequestException` from the
transport). In those cases `Status` is `-1` and the original exception is
available via `InnerException`.

## Properties carried on every exception

`MongrelDBException` exposes three read-only properties inherited by all
subclasses:

| Property | Type | Meaning |
|----------|------|---------|
| `Status` | `int` | The HTTP status code, or `-1` when unknown (client-side failure). |
| `Code` | `string?` | The server's structured error code, e.g. `"UNIQUE_VIOLATION"`, or `null`. |
| `OpIndex` | `int?` | The offending op index within a batch, or `null` when not reported. |

Plus the inherited `Message`, `InnerException`, and stack trace from
`Exception`.

The daemon's JSON error envelope (decoded into the properties above):

```json
{
  "status": "aborted",
  "error": {
    "code": "UNIQUE_VIOLATION",
    "message": "duplicate key in column 1",
    "op_index": 0
  }
}
```

Structured codes you will commonly see in `Code`:

| `Code` | Meaning |
|--------|---------|
| `UNIQUE_VIOLATION` | A unique/PK constraint rejected the commit |
| `FK_VIOLATION` | A foreign-key reference was missing |
| `CHECK_VIOLATION` | A check constraint or trigger rejected the commit |
| `NOT_FOUND` | A named resource (table, schema) does not exist |

## HTTP status → exception mapping

| HTTP status | Exception | Notes |
|-------------|-----------|-------|
| 401, 403 | `AuthException` | Bad/missing credentials |
| 404 | `NotFoundException` | Resource not found |
| 409 | `ConflictException` | Constraint violation at commit |
| 400 | `QueryException` | Malformed request / bad query |
| 5xx | `QueryException` | Daemon-side failure |
| other non-2xx | `QueryException` | Catch-all |
| 2xx | (no exception) | Success |
| transport failure | `QueryException` | `Status == -1`, `InnerException` set |

## Discriminating errors

### By type — catch the specific subclass

```csharp
try
{
    await db.GetSchemaForAsync("missing_table");
}
catch (NotFoundException)
{
    Console.WriteLine("table does not exist");
}
catch (ConflictException)
{
    Console.WriteLine("unexpected conflict on a read");
}
catch (AuthException)
{
    Console.WriteLine("bad credentials");
}
catch (QueryException e)
{
    Console.WriteLine($"server error or malformed request: {e.Message}");
}
```

Because all four subclasses share the parent, a single
`catch (MongrelDBException e)` handles everything if you only need to know it
failed. Use C# exception filters for finer control:

```csharp
catch (ConflictException e) when (e.Code == "UNIQUE_VIOLATION")
{
    // only unique violations
}
```

### By details — read the properties

```csharp
try
{
    await db.GetSchemaForAsync("missing_table");
}
catch (MongrelDBException e)
{
    Console.WriteLine($"status={e.Status} code={e.Code} op={e.OpIndex} msg={e.Message}");
}
```

Combine the two for constraint-aware handling:

```csharp
try
{
    await txn.CommitAsync();
}
catch (ConflictException e)
{
    Console.WriteLine($"constraint {e.Code} at op {e.OpIndex}: {e.Message}");
}
```

## Recovery patterns

### Auth failure — do not retry blindly

A retry will not fix bad credentials. Surface the error to the caller or
operator.

```csharp
try
{
    await db.PutAsync("orders", cells);
}
catch (AuthException e)
{
    // Refresh credentials from your secret store, or fail fast.
    throw new InvalidOperationException("credentials rejected; refresh token", e);
}
```

### Not found — fall back, do not crash

For lookups by primary key, a 404 may be a normal "absent" result.

```csharp
try
{
    var rows = await db.Query("orders")
        .Where("pk", new Dictionary<string, object?> { ["value"] = id })
        .ExecuteAsync();
    return rows;
}
catch (NotFoundException)
{
    return new List<Dictionary<string, object?>>(); // table missing — treat as empty
}
```

Note: a `pk` query against an existing table returns zero rows, not a 404;
`NotFoundException` here means the table itself is missing.

### Constraint conflict — report the offending op

```csharp
try
{
    await txn.CommitAsync();
}
catch (ConflictException e)
{
    if (e.OpIndex is int op)
    {
        throw new InvalidOperationException(
            $"op {op} violated {e.Code}: {e.Message}", e);
    }
    throw new InvalidOperationException($"conflict {e.Code}: {e.Message}", e);
}
```

The engine already rolled back the whole batch — there is nothing to undo.

### Transient failure — retry with an idempotency key

`QueryException` covers transport and 5xx failures. With an idempotency key,
retrying a transaction is safe (see [transactions.md](transactions.md)).

```csharp
public async Task RunAsync(Func<Transaction> build, string key, CancellationToken ct)
{
    try
    {
        var txn = build();
        await txn.CommitAsync(key, ct);
    }
    catch (AuthException) { throw; }   // not transient
    catch (ConflictException) { throw; } // not transient
    catch (OperationCanceledException) { throw; }
    catch (MongrelDBException)
    {
        // QueryException / network — caller may retry with the same key.
        throw;
    }
}
```

### Transaction-state error

`Transaction.CommitAsync` and `Transaction.Rollback` throw
`InvalidOperationException` ("mongreldb: transaction already committed") if
called twice. Fix the control flow rather than catching it.

```csharp
await txn.CommitAsync();
await txn.CommitAsync(); // throws InvalidOperationException — logic bug
```

### Cancellation

Every async method accepts a `CancellationToken`. Cancellation surfaces as
`OperationCanceledException` / `TaskCanceledException`, **not** as a
`MongrelDBException` — handle it separately when you need to.

```csharp
try
{
    await db.PutAsync("orders", cells, cancellationToken: ct);
}
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    // expected — the caller cancelled
}
```

## Quick reference

```csharp
using Visorcraft.MongrelDB;

// Type-based discrimination:
try
{
    await db.PutAsync("orders", cells);
}
catch (AuthException) { /* 401/403 */ }
catch (NotFoundException) { /* 404 */ }
catch (ConflictException e) { /* 409; e.Code, e.OpIndex */ }
catch (QueryException e) { /* 400/5xx/transport; e.Status == -1 for client-side */ }
catch (MongrelDBException e) { /* catch-all parent */ }

// Property access on any MongrelDBException:
//   e.Status    -> int  (HTTP status, or -1)
//   e.Code       -> string? ("UNIQUE_VIOLATION", ...)
//   e.OpIndex    -> int?   (offending op)
//   e.Message    -> string (human-readable message)
//   e.InnerException -> Exception? (transport cause for QueryException)
```

## Next steps

- [transactions.md](transactions.md) — constraint handling and retries in context
- [auth.md](auth.md) — credential management
