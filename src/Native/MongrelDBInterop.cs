using System.Runtime.InteropServices;

namespace Visorcraft.MongrelDB.Native;

/// <summary>
/// Low-level P/Invoke declarations for the MongrelDB C ABI (libmongreldb).
/// These map 1:1 to the functions declared in mongreldb_engine.h.
/// Application code should use <see cref="MongrelDBNative"/> instead.
/// </summary>
internal static class MongrelDBInterop
{
	private const string Lib = "mongreldb";

	// ── Error accessors ──────────────────────────────────────────────────

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern IntPtr mongreldb_last_error();

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_last_error_code();

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern void mongreldb_free_string(IntPtr ptr);

	// ── Database lifecycle ───────────────────────────────────────────────

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern IntPtr mongreldb_create([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern IntPtr mongreldb_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern IntPtr mongreldb_create_with_credentials(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string user,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string password);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern IntPtr mongreldb_open_with_credentials(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string user,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string password);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_database_close(IntPtr db);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern void mongreldb_database_free(IntPtr db);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_database_compact(IntPtr db);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_database_table_names(
		IntPtr db, out IntPtr outStr, out UIntPtr outLen);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_drop_table(
		IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_rename_table(
		IntPtr db,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string name,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string newName);

	// ── SQL execution ────────────────────────────────────────────────────

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_database_sql(
		IntPtr db,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
		out IntPtr outBuf,
		out UIntPtr outLen);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_database_sql_refresh(IntPtr db);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern void mongreldb_free_sql_result(IntPtr ptr, UIntPtr len);

	// ── Migration planning ───────────────────────────────────────────────

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_plan_migrations_json(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string appliedJson,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string desiredJson,
		out IntPtr outJson);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_migration_checksum_json(
		long version,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string name,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string opsJson,
		out IntPtr outChecksum);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern void mongreldb_free_migrate_string(IntPtr ptr);
}

/// <summary>
/// Low-level P/Invoke declarations for the MongrelDB Kit C ABI (libmongreldb_kit).
/// These map 1:1 to the functions declared in mongreldb_kit.h.
/// </summary>
internal static class MongrelDBKitInterop
{
	private const string Lib = "mongreldb_kit";

	// ── Error accessors ──────────────────────────────────────────────────

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern IntPtr mongreldb_kit_last_error();

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_kit_last_error_code();

	// ── Database lifecycle ───────────────────────────────────────────────

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern IntPtr mongreldb_kit_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern IntPtr mongreldb_kit_create(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string schemaJson);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern IntPtr mongreldb_kit_open_encrypted(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string passphrase);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern IntPtr mongreldb_kit_create_encrypted(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string schemaJson,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string passphrase);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern IntPtr mongreldb_kit_open_with_credentials(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string user,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string password);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern IntPtr mongreldb_kit_create_with_credentials(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string schemaJson,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string adminUser,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string adminPassword);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_kit_refresh_sql_session(IntPtr db);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern void mongreldb_kit_database_free(IntPtr db);

	// ── SQL execution ────────────────────────────────────────────────────

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_kit_sql_rows(
		IntPtr db,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
		out IntPtr outJson);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_kit_sql_arrow(
		IntPtr db,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
		out IntPtr outBuf,
		out UIntPtr outLen);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern void mongreldb_kit_free_arrow(IntPtr ptr, UIntPtr len);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern void mongreldb_kit_free_json(IntPtr ptr);

	// ── Migrations ───────────────────────────────────────────────────────

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_kit_migrate_json(
		IntPtr db,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string migrationsJson);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_kit_applied_migrations_json(
		IntPtr db,
		out IntPtr outJson);

	// ── Query builder ────────────────────────────────────────────────────

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_kit_query_select_json(
		IntPtr db,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string queryJson,
		out IntPtr outJson);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_kit_query_join_json(
		IntPtr db,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string queryJson,
		out IntPtr outJson);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_kit_query_aggregate_json(
		IntPtr db,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string queryJson,
		out IntPtr outJson);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_kit_query_insert_json(
		IntPtr db,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string queryJson,
		out IntPtr outJson);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_kit_query_update_json(
		IntPtr db,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string queryJson,
		out IntPtr outJson);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_kit_query_upsert_json(
		IntPtr db,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string queryJson,
		out IntPtr outJson);

	[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
	public static extern int mongreldb_kit_query_delete_json(
		IntPtr db,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string queryJson,
		out IntPtr outJson);
}
