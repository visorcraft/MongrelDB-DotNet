# Transactions

MongrelDB commits every write through a single atomic transaction endpoint
(`POST /kit/txn`). This guide covers the two ways to use it — a one-shot
single op, and a staged batch — plus idempotency keys for safe retries, typed
constraint-violation handling, and rollback.

The engine enforces `UNIQUE`, foreign-key, check, and trigger constraints at
**commit time**. A violation aborts the entire batch: no op in the batch
becomes visible, and `CommitAsync` throws `ConflictException`.

All write methods are async and accept a `CancellationToken`.

---

## Single puts vs. batch transactions

### Single op: `PutAsync`

`MongrelDBClient.PutAsync` is a convenience wrapper that sends a one-op
transaction. Use it when a write is independent and you do not need atomicity
across multiple rows.

```csharp
// One row, one atomic op. idempotencyKey defaults to null.
Dictionary<string, object?> res = await db.PutAsync(
    "orders",
    Cells.Of(1, 1L, 2, "Alice", 3, 99.5));
```

`UpsertAsync`, `DeleteAsync`, and `DeleteByPkAsync` are the same shape:
single-op transactions.

### Batch: `BeginTransaction` + `Transaction`

When several writes must succeed or fail together, stage them on a
`Transaction` and commit once. All ops go to the server in a single HTTP
request and commit atomically.

```csharp
var txn = db.BeginTransaction();
txn.Put("orders", Cells.Of(1, 10L, 2, "Dave", 3, 50.0), returning: false);
txn.Put("orders", Cells.Of(1, 11L, 2, "Eve", 3, 75.0), returning: false);
txn.DeleteByPk("orders", 2L);

List<Dictionary<string, object?>> results = await txn.CommitAsync(); // atomic — all or nothing
Console.WriteLine($"committed {results.Count} ops");
```

The third argument to `Transaction.Put` is `returning`. Set it to `true` to
have the daemon echo the written row back in the result map — useful for
reading server-assigned values.

```csharp
var txn = db.BeginTransaction();
txn.Put("orders", Cells.Of(1, 42L, 2, "Hal", 3, 12.0), returning: true);
var res = await txn.CommitAsync();
Console.WriteLine($"server echoed: {string.Join(", ", res[0])}");
```

`Transaction.Upsert(table, cells, updateCells, returning)` takes an
`updateCells` map applied on a primary-key conflict. A `null` `updateCells`
means "do nothing on conflict".

```csharp
txn.Upsert(
    "orders",
    Cells.Of(1, 1L, 2, "Alice", 3, 120.0),     // insert these...
    Cells.Of(3, 120.0),                         // ...or update only amount on conflict
    returning: false);
```

## Idempotency keys for safe retries

Networks drop requests and daemons crash after committing but before
replying. An idempotency key makes a commit safe to retry: the daemon
remembers the key and replays the **original** result on a duplicate commit,
even across restarts.

Pass the key as the `idempotencyKey` argument to `CommitAsync` (or to
`PutAsync`/`UpsertAsync`):

```csharp
// A web handler that must not double-charge, even if the client retries or
// the connection drops after the daemon committed.
public async Task ChargeAsync(string orderId, CancellationToken ct)
{
    var txn = db.BeginTransaction();
    txn.Put("charges", Cells.Of(1, orderId, 2, 199.0), returning: false);

    // Use a stable, business-meaningful key derived from the request.
    // On a retry with the same key the daemon returns the first commit's
    // result instead of inserting a second row.
    await txn.CommitAsync("charge:" + orderId, ct);
}
```

Rules for keys:

- Any non-empty string works. Prefer content-derived, globally-unique values
  (e.g. `"charge:" + orderId`).
- `null` (or the empty string) disables idempotency — a retry will commit
  again.
- The key scopes the **entire batch**, not individual ops. Reuse the exact
  same ops and key together when retrying.

A safe retry loop — build the transaction inside the loop so a failed attempt
can be retried cleanly:

```csharp
public async Task CommitWithRetryAsync(
    Func<Transaction> build,   // rebuilds the same staged ops on a fresh txn
    string key,
    CancellationToken ct)
{
    for (int attempt = 0; attempt < 3; attempt++)
    {
        try
        {
            var txn = build();
            await txn.CommitAsync(key, ct);
            return;
        }
        catch (ConflictException) { throw; }      // not transient
        catch (AuthException) { throw; }          // not transient
        catch (OperationCanceledException) { throw; }
        catch (MongrelDBException)
        {
            // QueryException / network — the idempotency key makes it safe to
            // retry.
            if (attempt == 2) throw;
            await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct); // 1s, 2s, 4s
        }
    }
}
```

## Handling constraint violations

Constraint violations arrive as HTTP 409, mapped to `ConflictException`. It
extends `MongrelDBException` and carries the structured `Code` and `OpIndex`
properties:

```csharp
var txn = db.BeginTransaction();
txn.Put("orders", Cells.Of(1, 1L), returning: false); // duplicate PK

try
{
    await txn.CommitAsync();
}
catch (ConflictException e)
{
    switch (e.Code ?? "")
    {
        case "UNIQUE_VIOLATION":
            Console.WriteLine($"duplicate at op {e.OpIndex}: {e.Message}");
            break;
        case "FK_VIOLATION":
            Console.WriteLine($"missing parent at op {e.OpIndex}: {e.Message}");
            break;
        case "CHECK_VIOLATION":
            Console.WriteLine($"check failed at op {e.OpIndex}: {e.Message}");
            break;
        default:
            Console.WriteLine($"other conflict: {e.Message}");
            break;
    }
}
```

The error envelope from the daemon looks like:

```json
{"status": "aborted", "error": {"code": "UNIQUE_VIOLATION", "message": "...", "op_index": 0}}
```

`OpIndex` points at the offending op within the batch so you can report which
row caused the failure. It is `null` when the server did not report one.

For simple category checks, catch the specific subclass:

```csharp
try
{
    await txn.CommitAsync();
}
catch (ConflictException e)
{
    // any constraint violation
}
catch (NotFoundException e)
{
    // table or row missing
}
catch (AuthException e)
{
    // bad credentials
}
```

## Rollback after failure

There are two notions of "rollback":

1. **Server-side.** When `CommitAsync` throws `ConflictException`, the engine
   has already discarded the entire batch. Nothing was written; there is no
   server rollback to perform.
2. **Client-side.** `Transaction.Rollback()` clears the locally staged ops.
   Call it to release the `Transaction` when you decide not to commit (for
   example, after a validation error in your own code, before ever sending).

```csharp
var txn = db.BeginTransaction();
txn.Put("orders", Cells.Of(1, 1L, 2, "Iris", 3, 5.0), returning: false);

if (!BusinessRuleOk())
{
    // Throw the staged ops away locally. Nothing has been sent to the daemon.
    txn.Rollback();
    return;
}

try
{
    await txn.CommitAsync();
}
catch (ConflictException e)
{
    // On conflict the server already rolled back. No client-side cleanup of
    // server data is needed.
    Console.Error.WriteLine($"conflict: {e.Message}");
}
```

`Rollback` and `CommitAsync` both throw `InvalidOperationException`
("mongreldb: transaction already committed") if the transaction was already
committed or rolled back. Treat that as a programming error to fix upstream,
not a runtime condition to silence.

### Recovering from a failed batch

Because a failed commit rejects the whole batch, the usual recovery is to
re-issue the ops that are still valid, optionally splitting out the offender.
Keep your own list of the logical ops if you need surgical retry, since
`Transaction` does not expose its staged ops.

## Summary

| Goal | Use |
|------|-----|
| One independent write | `PutAsync` / `UpsertAsync` / `DeleteAsync` / `DeleteByPkAsync` |
| Several writes that must commit together | `BeginTransaction` + `CommitAsync` |
| Retry safely after a network blip | `CommitAsync(idempotencyKey)` with a stable key |
| Distinguish constraint classes | catch `ConflictException`, read `.Code` / `.OpIndex` |
| Abort before sending | `Transaction.Rollback()` |

See [errors.md](errors.md) for the full exception hierarchy and [queries.md](queries.md)
for read patterns.
