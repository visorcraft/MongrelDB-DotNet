namespace Visorcraft.MongrelDB;

/// <summary>
/// Stages operations locally and commits them atomically in a single
/// <c>/kit/txn</c> request. The engine enforces unique, foreign-key, check, and
/// trigger constraints at commit time; on any violation all operations roll
/// back and <see cref="CommitAsync"/> throws a <see cref="ConflictException"/>
/// carrying the server's structured error code and offending op index.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Transaction"/> is single-use: after <see cref="CommitAsync"/> or
/// <see cref="Rollback"/> it must not be reused. Calling <see cref="CommitAsync"/>
/// or <see cref="Rollback"/> a second time throws
/// <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// Start one with <see cref="MongrelDBClient.BeginTransaction"/>:
/// </para>
/// <code>
/// var txn = db.BeginTransaction();
/// txn.Put("orders", Cells.Of(1, 10L, 2, "Dave"), returning: false);
/// txn.Put("orders", Cells.Of(1, 11L, 2, "Eve"), returning: false);
/// txn.DeleteByPk("orders", 2L);
/// var results = await txn.CommitAsync(); // atomic - all or nothing
/// </code>
/// </remarks>
public sealed class Transaction
{
    private readonly MongrelDBClient _client;
    private readonly List<Dictionary<string, object?>> _ops = new();
    private bool _committed;

    internal Transaction(MongrelDBClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Stages an insert. <paramref name="returning"/>, when <see langword="true"/>,
    /// asks the daemon to echo the row in the per-operation result.
    /// </summary>
    /// <param name="table">The target table.</param>
    /// <param name="cells">A column-id-to-value map.</param>
    /// <param name="returning">Whether to echo the row in the result.</param>
    /// <returns>This transaction, for chaining.</returns>
    public Transaction Put(string table, Cells cells, bool returning)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(cells);
        var op = new Dictionary<string, object?>
        {
            ["put"] = new Dictionary<string, object?>
            {
                ["table"] = table,
                ["cells"] = MongrelDBClient.FlattenCells(cells),
                ["returning"] = returning,
            },
        };
        _ops.Add(op);
        return this;
    }

    /// <summary>
    /// Stages an insert-or-update. <paramref name="updateCells"/>, when non-null,
    /// supplies the values written on a primary-key conflict; null means DO
    /// NOTHING.
    /// </summary>
    /// <param name="table">The target table.</param>
    /// <param name="cells">The column-id-to-value map to insert.</param>
    /// <param name="updateCells">The values written on conflict, or null.</param>
    /// <param name="returning">Whether to echo the row in the result.</param>
    /// <returns>This transaction, for chaining.</returns>
    public Transaction Upsert(string table, Cells cells, Cells? updateCells, bool returning)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(cells);
        var upsert = new Dictionary<string, object?>
        {
            ["table"] = table,
            ["cells"] = MongrelDBClient.FlattenCells(cells),
            ["returning"] = returning,
        };
        if (updateCells is not null)
        {
            upsert["update_cells"] = MongrelDBClient.FlattenCells(updateCells);
        }
        var op = new Dictionary<string, object?> { ["upsert"] = upsert };
        _ops.Add(op);
        return this;
    }

    /// <summary>
    /// Stages a delete by the internal row id.
    /// </summary>
    /// <param name="table">The target table.</param>
    /// <param name="rowId">The internal row id.</param>
    /// <returns>This transaction, for chaining.</returns>
    public Transaction Delete(string table, long rowId)
    {
        ArgumentNullException.ThrowIfNull(table);
        var op = new Dictionary<string, object?>
        {
            ["delete"] = new Dictionary<string, object?>
            {
                ["table"] = table,
                ["row_id"] = rowId,
            },
        };
        _ops.Add(op);
        return this;
    }

    /// <summary>
    /// Stages a delete by primary-key value.
    /// </summary>
    /// <param name="table">The target table.</param>
    /// <param name="pk">The primary-key value.</param>
    /// <returns>This transaction, for chaining.</returns>
    public Transaction DeleteByPk(string table, object? pk)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(pk);
        var op = new Dictionary<string, object?>
        {
            ["delete_by_pk"] = new Dictionary<string, object?>
            {
                ["table"] = table,
                ["pk"] = pk,
            },
        };
        _ops.Add(op);
        return this;
    }

    /// <summary>
    /// The number of staged operations.
    /// </summary>
    public int Count => _ops.Count;

    /// <summary>
    /// Sends all staged operations atomically and returns the per-operation
    /// results. <paramref name="idempotencyKey"/>, when non-null and non-empty,
    /// makes the commit safe to retry - the daemon returns the original
    /// response on duplicate commits, even after a crash.
    /// </summary>
    /// <param name="idempotencyKey">An idempotency key, or null.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The per-operation results, or an empty list if nothing was staged.</returns>
    /// <exception cref="InvalidOperationException">Thrown if called twice on the same transaction.</exception>
    /// <exception cref="ConflictException">Thrown if a constraint violation rolled back the batch.</exception>
    public async Task<List<Dictionary<string, object?>>> CommitAsync(string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        if (_committed)
        {
            throw new InvalidOperationException("mongreldb: transaction already committed");
        }
        _committed = true;
        if (_ops.Count == 0)
        {
            return new List<Dictionary<string, object?>>();
        }
        return await _client.CommitTxnAsync(_ops, idempotencyKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Discards all staged operations.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the transaction was already committed.</exception>
    public void Rollback()
    {
        if (_committed)
        {
            throw new InvalidOperationException("mongreldb: transaction already committed");
        }
        _ops.Clear();
        _committed = true;
    }
}
