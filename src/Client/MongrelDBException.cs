namespace Visorcraft.MongrelDB;

/// <summary>
/// Base class for all errors raised by the MongrelDB client.
/// </summary>
/// <remarks>
/// <para>
/// Every non-2xx response from the daemon is mapped to a typed subclass of this
/// exception. Catch <see cref="MongrelDBException"/> to handle any client-side
/// failure, or catch one of the specific subclasses:
/// </para>
/// <list type="bullet">
///   <item><see cref="AuthException"/> - HTTP 401/403 (bad or missing credentials)</item>
///   <item><see cref="NotFoundException"/> - HTTP 404 (missing table, schema, etc.)</item>
///   <item><see cref="ConflictException"/> - HTTP 409 (unique, foreign-key, check, or
///       trigger constraint violations)</item>
///   <item><see cref="QueryException"/> - HTTP 400 or 5xx, and any other request-level
///       failure not covered by the more specific subclasses</item>
/// </list>
/// <para>
/// Each typed exception also carries the HTTP status code, the daemon's decoded
/// error envelope (message, structured code, and offending op index), so callers
/// can both branch on type and inspect the response detail.
/// </para>
/// </remarks>
public class MongrelDBException : Exception
{
    /// <summary>HTTP status code returned by the daemon, or <c>-1</c> when unknown.</summary>
    public int Status { get; }

    /// <summary>The server's structured error code, when present (e.g. <c>UNIQUE_VIOLATION</c>).</summary>
    public string? Code { get; }

    /// <summary>The offending operation index within a transaction, when the server reports one.</summary>
    public int? OpIndex { get; }

    /// <summary>Constructs a new exception with a message and no HTTP detail.</summary>
    public MongrelDBException(string message)
        : this(message, status: -1, code: null, opIndex: null, inner: null) { }

    /// <summary>Constructs a new exception with a message and a cause.</summary>
    public MongrelDBException(string message, Exception inner)
        : this(message, status: -1, code: null, opIndex: null, inner: inner) { }

    /// <summary>Constructs a new exception carrying the daemon's HTTP response detail.</summary>
    /// <param name="message">The human-readable error message.</param>
    /// <param name="status">The HTTP status code, or <c>-1</c> when unknown.</param>
    /// <param name="code">The server's structured error code, or null.</param>
    /// <param name="opIndex">The offending op index within a transaction, or null.</param>
    public MongrelDBException(string message, int status, string? code, int? opIndex)
        : this(message, status, code, opIndex, inner: null) { }

    /// <summary>Constructs a new exception carrying the daemon's HTTP response detail and a cause.</summary>
    protected MongrelDBException(string message, int status, string? code, int? opIndex, Exception? inner)
        : base(message, inner)
    {
        Status = status;
        Code = code;
        OpIndex = opIndex;
    }
}

/// <summary>
/// Raised for HTTP 401 or 403 responses - bad or missing credentials.
/// </summary>
/// <remarks>
/// The daemon returns these when started in <c>--auth-token</c> or
/// <c>--auth-users</c> mode and the request lacks valid credentials.
/// </remarks>
public class AuthException : MongrelDBException
{
    internal AuthException(string message, int status, string? code, int? opIndex)
        : base(message, status, code, opIndex) { }
}

/// <summary>
/// Raised for HTTP 404 responses - a missing table, schema, or other resource.
/// </summary>
public class NotFoundException : MongrelDBException
{
    internal NotFoundException(string message, int status, string? code, int? opIndex)
        : base(message, status, code, opIndex) { }
}

/// <summary>
/// Raised for HTTP 409 responses - a unique, foreign-key, check, or trigger
/// constraint violation.
/// </summary>
/// <remarks>
/// During a transaction commit, the engine enforces all constraints at commit
/// time. On any violation every staged operation rolls back and this exception
/// is thrown carrying the server's structured <see cref="MongrelDBException.Code"/>
/// (e.g. <c>UNIQUE_VIOLATION</c>, <c>FK_VIOLATION</c>) and the offending
/// <see cref="MongrelDBException.OpIndex"/> within the batch.
/// </remarks>
public class ConflictException : MongrelDBException
{
    internal ConflictException(string message, int status, string? code, int? opIndex)
        : base(message, status, code, opIndex) { }
}

/// <summary>
/// Raised for HTTP 400 or 5xx responses, and for any other request-level failure
/// not covered by <see cref="AuthException"/>, <see cref="NotFoundException"/>,
/// or <see cref="ConflictException"/>.
/// </summary>
/// <remarks>
/// This is the catch-all for malformed queries, server-side errors, and
/// transport failures (the latter carries the underlying <see cref="Exception"/>
/// cause via <see cref="Exception.InnerException"/> and an HTTP status of
/// <c>-1</c>).
/// </remarks>
public class QueryException : MongrelDBException
{
    internal QueryException(string message, int status, string? code, int? opIndex)
        : base(message, status, code, opIndex) { }

    internal QueryException(string message, Exception inner)
        : base(message, inner) { }

    internal QueryException(string message)
        : base(message) { }
}
