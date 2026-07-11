using System.Runtime.InteropServices;
using System.Text.Json;

namespace Visorcraft.MongrelDB.Native;

/// <summary>
/// In-process embedded MongrelDB database via the native C ABI. No daemon,
/// no HTTP overhead - the engine runs directly in the process via P/Invoke
/// into libmongreldb and libmongreldb_kit.
/// </summary>
/// <remarks>
/// <para>
/// This class wraps the Kit layer (<c>libmongreldb_kit</c>), which provides
/// schema-aware operations: SQL execution, the query builder, migration
/// runner, and row validation. For lower-level engine access (typed table
/// operations, the single-table query builder, auth), use the core engine
/// directly via <see cref="MongrelDBInterop"/>.
/// </para>
/// <para>
/// The database handle is owned by this instance and freed on <see cref="Dispose"/>.
/// The handle is NOT thread-safe - create one <see cref="MongrelDBNative"/>
/// per thread if you need concurrency.
/// </para>
/// </remarks>
public sealed class MongrelDBNative : IDisposable
{
	private IntPtr _handle;
	private bool _disposed;

	/// <summary>
	/// Opens an existing Kit database from disk. Creates it if it doesn't exist.
	/// </summary>
	/// <param name="path">Filesystem path to the database directory.</param>
	public MongrelDBNative(string path)
	{
		NativeLibraryLoader.EnsureInitialized();
		_handle = MongrelDBKitInterop.mongreldb_kit_open(path);
		if (_handle == IntPtr.Zero)
			throw new QueryException(LastKitError(), -1, null, null);
	}

	/// <summary>
	/// Creates a fresh Kit database with the given JSON schema.
	/// </summary>
	/// <param name="path">Filesystem path for the new database.</param>
	/// <param name="schemaJson">Kit schema as JSON (see the Kit schema format).</param>
	public static MongrelDBNative Create(string path, string schemaJson)
	{
		NativeLibraryLoader.EnsureInitialized();
		var handle = MongrelDBKitInterop.mongreldb_kit_create(path, schemaJson);
		if (handle == IntPtr.Zero)
			throw new QueryException(LastKitError(), -1, null, null);
		return new MongrelDBNative { _handle = handle };
	}

	// ── SQL ──────────────────────────────────────────────────────────────

	/// <summary>
	/// Runs a SQL statement and returns the result as a JSON array of row
	/// objects (column name -> value). DDL/DML returns an empty list.
	/// </summary>
	public List<Dictionary<string, object?>> SqlRows(string sql)
	{
		CheckDisposed();
		int rc = MongrelDBKitInterop.mongreldb_kit_sql_rows(_handle, sql, out IntPtr jsonPtr);
		if (rc != 0)
			throw new QueryException(LastKitError(), -1, null, null);

		try
		{
			if (jsonPtr == IntPtr.Zero)
				return [];

			var json = PtrToUtf8String(jsonPtr);
			if (string.IsNullOrWhiteSpace(json))
				return [];

			using var doc = JsonDocument.Parse(json);
			var result = new List<Dictionary<string, object?>>();
			foreach (var row in doc.RootElement.EnumerateArray())
			{
				var dict = new Dictionary<string, object?>();
				foreach (var prop in row.EnumerateObject())
					dict[prop.Name] = JsonElementToObject(prop.Value);
				result.Add(dict);
			}
			return result;
		}
		finally
		{
			MongrelDBKitInterop.mongreldb_kit_free_json(jsonPtr);
		}
	}

	/// <summary>
	/// Runs a SQL statement and returns the result as Arrow IPC file bytes
	/// (starts with "ARROW1" magic). DDL/DML returns an empty array.
	/// Decode with any Arrow IPC reader.
	/// </summary>
	public byte[] SqlArrow(string sql)
	{
		CheckDisposed();
		int rc = MongrelDBKitInterop.mongreldb_kit_sql_arrow(_handle, sql, out IntPtr buf, out UIntPtr len);
		if (rc != 0)
			throw new QueryException(LastKitError(), -1, null, null);
		try
		{
			if (buf == IntPtr.Zero || len == UIntPtr.Zero)
				return [];
			return PtrToByteArray(buf, (int)len);
		}
		finally
		{
			MongrelDBKitInterop.mongreldb_kit_free_arrow(buf, len);
		}
	}

	// ── Migrations ───────────────────────────────────────────────────────

	/// <summary>
	/// Runs the Kit migration runner with the given JSON array of Migration
	/// objects. Already-applied migrations are skipped.
	/// </summary>
	public void Migrate(string migrationsJson)
	{
		CheckDisposed();
		int rc = MongrelDBKitInterop.mongreldb_kit_migrate_json(_handle, migrationsJson);
		if (rc != 0)
			throw new QueryException(LastKitError(), -1, null, null);
	}

	/// <summary>
	/// Reads the list of migrations already applied to the database, as a
	/// JSON array of Migration objects.
	/// </summary>
	public string AppliedMigrationsJson()
	{
		CheckDisposed();
		int rc = MongrelDBKitInterop.mongreldb_kit_applied_migrations_json(_handle, out IntPtr jsonPtr);
		if (rc != 0)
			throw new QueryException(LastKitError(), -1, null, null);
		try
		{
			return jsonPtr == IntPtr.Zero ? "[]" : PtrToUtf8String(jsonPtr);
		}
		finally
		{
			MongrelDBKitInterop.mongreldb_kit_free_json(jsonPtr);
		}
	}

	/// <summary>
	/// Rebuild the cached SQL session so it sees the current table set.
	/// Call after a migration that creates or drops tables.
	/// </summary>
	public void RefreshSqlSession()
	{
		CheckDisposed();
		int rc = MongrelDBKitInterop.mongreldb_kit_refresh_sql_session(_handle);
		if (rc != 0)
			throw new QueryException(LastKitError(), -1, null, null);
	}

	// ── Query builder ────────────────────────────────────────────────────

	/// <summary>
	/// Runs a SELECT query via the Kit query builder. Takes a JSON-encoded
	/// Select AST, returns the matching rows.
	/// </summary>
	public List<Dictionary<string, object?>> QuerySelect(string selectJson)
	{
		return QueryJson(MongrelDBKitInterop.mongreldb_kit_query_select_json, selectJson);
	}

	/// <summary>
	/// Runs a JOIN query via the Kit query builder. Takes a JSON-encoded
	/// JoinQuery AST, returns the merged rows.
	/// </summary>
	public List<Dictionary<string, object?>> QueryJoin(string joinJson)
	{
		return QueryJson(MongrelDBKitInterop.mongreldb_kit_query_join_json, joinJson);
	}

	/// <summary>
	/// Runs an AGGREGATE query via the Kit query builder.
	/// </summary>
	public List<Dictionary<string, object?>> QueryAggregate(string aggregateJson)
	{
		return QueryJson(MongrelDBKitInterop.mongreldb_kit_query_aggregate_json, aggregateJson);
	}

	/// <summary>
	/// Runs an INSERT query. Returns the returning values (or empty list).
	/// </summary>
	public string QueryInsert(string insertJson)
	{
		return QueryJsonRaw(MongrelDBKitInterop.mongreldb_kit_query_insert_json, insertJson);
	}

	/// <summary>
	/// Runs an UPDATE query. Returns the returning values (or empty list).
	/// </summary>
	public string QueryUpdate(string updateJson)
	{
		return QueryJsonRaw(MongrelDBKitInterop.mongreldb_kit_query_update_json, updateJson);
	}

	/// <summary>
	/// Runs an UPSERT query. Returns the returning values (or empty list).
	/// </summary>
	public string QueryUpsert(string upsertJson)
	{
		return QueryJsonRaw(MongrelDBKitInterop.mongreldb_kit_query_upsert_json, upsertJson);
	}

	/// <summary>
	/// Runs a DELETE query. Returns the returning values (or empty list).
	/// </summary>
	public string QueryDelete(string deleteJson)
	{
		return QueryJsonRaw(MongrelDBKitInterop.mongreldb_kit_query_delete_json, deleteJson);
	}

	// ── Core engine: migration planning (no live database needed) ────────

	/// <summary>
	/// Plans pending migrations. Takes applied and desired migration lists
	/// (JSON arrays), returns the pending migrations as a JSON array.
	/// </summary>
	public static string PlanMigrationsJson(string appliedJson, string desiredJson)
	{
		NativeLibraryLoader.EnsureInitialized();
		int rc = MongrelDBInterop.mongreldb_plan_migrations_json(appliedJson, desiredJson, out IntPtr outJson);
		if (rc != 0)
			throw new QueryException(LastCoreError(), -1, null, null);
		try
		{
			return outJson == IntPtr.Zero ? "[]" : PtrToUtf8String(outJson);
		}
		finally
		{
			MongrelDBInterop.mongreldb_free_migrate_string(outJson);
		}
	}

	/// <summary>
	/// Computes the canonical SHA-256 checksum for a single migration.
	/// </summary>
	public static string MigrationChecksum(long version, string name, string opsJson)
	{
		NativeLibraryLoader.EnsureInitialized();
		int rc = MongrelDBInterop.mongreldb_migration_checksum_json(version, name, opsJson, out IntPtr outChecksum);
		if (rc != 0)
			throw new QueryException(LastCoreError(), -1, null, null);
		try
		{
			return outChecksum == IntPtr.Zero ? "" : PtrToUtf8String(outChecksum);
		}
		finally
		{
			MongrelDBInterop.mongreldb_free_migrate_string(outChecksum);
		}
	}

	// ── Core engine: direct SQL (for when you don't want the Kit layer) ──

	/// <summary>
	/// Runs SQL directly against the core engine (no Kit layer). Returns
	/// Arrow IPC file bytes. Useful for raw engine access without Kit overhead.
	/// Requires that tables were created via the core engine, not via Kit.
	/// </summary>
	public byte[] CoreSqlArrow(IntPtr coreDbHandle, string sql)
	{
		int rc = MongrelDBInterop.mongreldb_database_sql(coreDbHandle, sql, out IntPtr buf, out UIntPtr len);
		if (rc != 0)
			throw new QueryException(LastCoreError(), -1, null, null);
		try
		{
			if (buf == IntPtr.Zero || len == UIntPtr.Zero)
				return [];
			return PtrToByteArray(buf, (int)len);
		}
		finally
		{
			MongrelDBInterop.mongreldb_free_sql_result(buf, len);
		}
	}

	// ── IDisposable ──────────────────────────────────────────────────────

	public void Dispose()
	{
		if (_disposed)
			return;
		if (_handle != IntPtr.Zero)
		{
			MongrelDBKitInterop.mongreldb_kit_database_free(_handle);
			_handle = IntPtr.Zero;
		}
		_disposed = true;
	}

	// ── Helpers ──────────────────────────────────────────────────────────

	private delegate int QueryJsonDelegate(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string json, out IntPtr outJson);

	private List<Dictionary<string, object?>> QueryJson(QueryJsonDelegate fn, string queryJson)
	{
		CheckDisposed();
		int rc = fn(_handle, queryJson, out IntPtr jsonPtr);
		if (rc != 0)
			throw new QueryException(LastKitError(), -1, null, null);
		try
		{
			if (jsonPtr == IntPtr.Zero)
				return [];
			var json = PtrToUtf8String(jsonPtr);
			if (string.IsNullOrWhiteSpace(json))
				return [];
			using var doc = JsonDocument.Parse(json);
			var result = new List<Dictionary<string, object?>>();
			foreach (var row in doc.RootElement.EnumerateArray())
			{
				var dict = new Dictionary<string, object?>();
				foreach (var prop in row.EnumerateObject())
					dict[prop.Name] = JsonElementToObject(prop.Value);
				result.Add(dict);
			}
			return result;
		}
		finally
		{
			MongrelDBKitInterop.mongreldb_kit_free_json(jsonPtr);
		}
	}

	private string QueryJsonRaw(QueryJsonDelegate fn, string queryJson)
	{
		CheckDisposed();
		int rc = fn(_handle, queryJson, out IntPtr jsonPtr);
		if (rc != 0)
			throw new QueryException(LastKitError(), -1, null, null);
		try
		{
			return jsonPtr == IntPtr.Zero ? "[]" : PtrToUtf8String(jsonPtr);
		}
		finally
		{
			MongrelDBKitInterop.mongreldb_kit_free_json(jsonPtr);
		}
	}

	private void CheckDisposed()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
	}

	private static string LastKitError()
	{
		IntPtr ptr = MongrelDBKitInterop.mongreldb_kit_last_error();
		return ptr == IntPtr.Zero ? "unknown error" : PtrToUtf8String(ptr);
	}

	private static string LastCoreError()
	{
		IntPtr ptr = MongrelDBInterop.mongreldb_last_error();
		return ptr == IntPtr.Zero ? "unknown error" : PtrToUtf8String(ptr);
	}

	private static string PtrToUtf8String(IntPtr ptr)
	{
		if (ptr == IntPtr.Zero)
			return "";
		// Find the NUL terminator to get the length, then copy.
		int len = 0;
		unsafe
		{
			byte* p = (byte*)ptr;
			while (p[len] != 0)
				len++;
		}
		return Marshal.PtrToStringUTF8(ptr, len);
	}

	private static byte[] PtrToByteArray(IntPtr ptr, int len)
	{
		byte[] buf = new byte[len];
		Marshal.Copy(ptr, buf, 0, len);
		return buf;
	}

	private static object? JsonElementToObject(JsonElement el)
	{
		return el.ValueKind switch
		{
			JsonValueKind.String => el.GetString(),
			JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.Null => null,
			_ => el.GetRawText(),
		};
	}
}
