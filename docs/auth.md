# Authentication & Authorization

A `mongreldb-server` daemon runs in one of three modes:

1. **Open** (default) - no auth required.
2. **Bearer token** (`--auth-token <TOKEN>`) - every request must carry an
   `Authorization: Bearer <TOKEN>` header.
3. **HTTP Basic** (`--auth-users`) - every request must carry an
   `Authorization: Basic <base64(user:pass)>` header.

The .NET client supports all three through its constructors. This guide shows
each mode, how to inspect what was sent, and how to manage users and roles via
SQL when the server is in Basic mode.

---

## Bearer token mode

Start the daemon with a token:

```sh
mongreldb-server --auth-token s3cret-token
```

Connect with the four-argument (or five-argument) constructor, passing the
token as the second argument. The token is sent as `Authorization: Bearer ...`
on every request.

```csharp
using var db = new MongrelDBClient(
    "http://127.0.0.1:8453",
    token: "s3cret-token",
    username: null,
    password: null);

if (!await db.HealthAsync())
{
    // A bad/missing token surfaces as AuthException on the first call;
    // HealthAsync swallows it and returns false.
    throw new InvalidOperationException("daemon not reachable (bad token?)");
}
```

A missing or wrong token surfaces as `AuthException` (HTTP 401/403) on any
call. `HealthAsync()` catches exceptions and returns `false`, so it is a safe
probe; other methods throw.

### Where the token comes from

Hard-coding secrets in source is bad practice. Read it from the environment
or .NET's user-secrets / configuration:

```csharp
string? token = Environment.GetEnvironmentVariable("MONGRELDB_TOKEN");
if (string.IsNullOrEmpty(token))
{
    throw new InvalidOperationException("MONGRELDB_TOKEN not set");
}
using var db = new MongrelDBClient(MongrelDBClient.DefaultBaseURL, token, null, null);
```

For ASP.NET Core apps, bind it from `IConfiguration` and inject a singleton
`MongrelDBClient`.

## Basic auth mode

Start the daemon with a users file or inline users:

```sh
mongreldb-server --auth-users
```

Connect with username and password:

```csharp
using var db = new MongrelDBClient(
    "http://127.0.0.1:8453",
    token: null,
    username: "admin",
    password: "s3cret");
```

The client base64-encodes `username:password` and sets `Authorization: Basic
...` on every request.

## Token takes precedence

If you supply both a token and Basic credentials, the token wins and Basic
credentials are ignored. This lets you layer an override without branching:

```csharp
using var db = new MongrelDBClient(
    baseUrl: url,
    token: "overrides-everything", // token wins
    username: "fallback",          // ignored
    password: "user");             // ignored
```

## Custom HttpClient and ownership

The five-argument constructor accepts a custom `HttpClient` - use it for a
custom `HttpClientHandler` (TLS, cookies), a proxy, an `HttpClientFactory`
integration, or a named timeout policy. When you pass your own instance, the
caller retains ownership and `Dispose()` will **not** dispose it (preventing
the well-known socket-exhaustion pitfall of disposing shared handlers):

```csharp
// Recommended in ASP.NET Core: register HttpClient via IHttpClientFactory
// and pass a (not-disposed-by-you) instance to the client.
var http = httpClientFactory.CreateClient("MongrelDB");
http.Timeout = TimeSpan.FromSeconds(10);

var db = new MongrelDBClient(url, token, username: null, password: null, http);
// db.Dispose() will NOT dispose `http` - the factory manages its lifetime.
```

When you do not pass an `HttpClient`, the client creates one with a 30-second
timeout and disposes it from `Dispose()`. Wrap construction in a `using`
scope:

```csharp
using var db = new MongrelDBClient(url, token, null, null);
```

## Verifying what gets sent

The auth header is applied in `ApplyAuth`, called from every request. For
debugging, point the client at a local echo server or watch the daemon logs.
A quick check that the configured identity is in use:

```csharp
Console.WriteLine($"Connecting as token? {!string.IsNullOrEmpty(token)}");
bool ok = await db.HealthAsync();
```

## User and role management via SQL

When the daemon is in Basic auth mode, users and roles live in the catalog
and are managed with SQL. Run these statements through `SqlAsync`.

### Create a user

```csharp
await db.SqlAsync("CREATE USER alice WITH PASSWORD 'hunter2'");
```

### Alter a user

Change a password:

```csharp
await db.SqlAsync("ALTER USER alice WITH PASSWORD 'new-password'");
```

Grant the admin role:

```csharp
await db.SqlAsync("ALTER USER alice ADMIN");
```

`ALTER USER ... ADMIN` is how you promote a user to full administrative
privileges (table creation/drop, compaction, user management). Use it
sparingly.

### Drop a user

```csharp
await db.SqlAsync("DROP USER alice");
```

### Roles and grants

```csharp
await db.SqlAsync("CREATE ROLE analyst");
await db.SqlAsync("GRANT SELECT ON orders TO analyst");
await db.SqlAsync("GRANT analyst TO alice");
await db.SqlAsync("REVOKE SELECT ON orders FROM analyst");
await db.SqlAsync("DROP ROLE analyst");
```

Exact grant syntax mirrors the server's SQL flavor; consult the server's SQL
reference for the full `GRANT`/`REVOKE` grammar available in your build.

## Common pitfalls

**Auth errors look like other errors without typed catches.** A 401/403
raises `AuthException`; a 404 raises `NotFoundException`. Always catch the
specific subclass rather than reading the message string.

**Forgetting to set auth in production.** A client built with
`new MongrelDBClient(url)` sends no credentials. Against an auth-enabled
daemon, every call throws `AuthException`. Centralize client construction (in
DI) so the auth credentials are never accidentally dropped.

**Sharing one client across threads is fine; sharing credentials across users
is not.** A `MongrelDBClient` is thread-safe, but it carries one identity. If
you serve multiple authenticated users, build a client per user (or per
request) with that user's token.

**Disposing a shared HttpClient.** If you pass your own `HttpClient` (e.g. from
`IHttpClientFactory`), do not let `using var db` dispose it unexpectedly -
the client intentionally avoids disposing externally-owned handlers, but make
sure your own code does not dispose the handler underneath.

**Token in version control.** Put secrets in the environment, user-secrets, a
secret manager, or a file outside the repo. Never commit a real token.

## Next steps

- [errors.md](errors.md) - `AuthException` and the rest of the exception hierarchy
- [quickstart.md](quickstart.md) - the full end-to-end walkthrough
