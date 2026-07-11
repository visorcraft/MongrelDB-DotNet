using Xunit;
using Xunit.Sdk;
using System.Runtime.InteropServices;
using Visorcraft.MongrelDB.Native;

namespace MongrelDB.Native.Tests;

/// <summary>
/// Tests for the native embedded mode (libmongreldb_kit via P/Invoke).
///
/// Offline tests always run. Live tests self-skip when the native library
/// cannot be loaded (e.g. when the runtimes/&lt;rid&gt;/native/ assets are
/// not present in this build).
/// </summary>
public class NativeTests
{
	private static readonly string SchemaJson = """
		{
			"tables": [{
				"id": 1,
				"name": "users",
				"columns": [
					{"id":1,"name":"id","storage_type":"int64","application_type":"int64","nullable":false,"primary_key":true,"default":null,"generated":false},
					{"id":2,"name":"name","storage_type":"text","application_type":"text","nullable":true,"primary_key":false,"default":null,"generated":false}
				],
				"primary_key": ["id"]
			}]
		}
		""";

	private static bool NativeLibAvailable()
	{
		try
		{
			NativeLibraryLoader.EnsureInitialized();
			// Try to resolve the Kit library (the main dependency).
			return NativeLibrary.TryLoad("mongreldb_kit", typeof(MongrelDBKitInterop).Assembly, null, out _);
		}
		catch
		{
			return false;
		}
	}

	private static void RequireNative()
	{
		if (!NativeLibAvailable())
			throw new SkipException("native library (libmongreldb_kit) not available");
	}

	private static string MakeTempDir()
	{
		var dir = Path.Combine(Path.GetTempPath(), "mdb_native_test_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	// ── Offline tests ────────────────────────────────────────────────────

	[Fact]
	public void MigrationChecksum_Produces_Valid_Hex()
	{
		// This exercises the core FFI (libmongreldb), not the Kit layer.
		// It should work whenever libmongreldb is loadable.
		try
		{
			NativeLibraryLoader.EnsureInitialized();
		}
		catch
		{
			throw new SkipException("native library not available");
		}

		var checksum = MongrelDBNative.MigrationChecksum(
			1, "initial", """[{"create_table":{"name":"users"}}]""");

		Assert.Equal(64, checksum.Length);
		Assert.True(checksum.All(c => "0123456789abcdef".Contains(c)),
			$"checksum should be hex: {checksum}");
	}

	[Fact]
	public void PlanMigrations_Returns_Pending()
	{
		try
		{
			NativeLibraryLoader.EnsureInitialized();
		}
		catch
		{
			throw new SkipException("native library not available");
		}

		var applied = "[]";
		var desired = """
			[
				{"version":1,"name":"initial","ops":[{"create_table":{"name":"users"}}]},
				{"version":2,"name":"add_idx","ops":[{"add_index":{"table":"users","index":"idx"}}]}
			]
			""";

		var pending = MongrelDBNative.PlanMigrationsJson(applied, desired);

		Assert.Contains("\"version\":1", pending);
		Assert.Contains("\"version\":2", pending);
	}

	// ── Live tests (self-skip if native lib not loaded) ──────────────────

	[SkippableFact]
	public void Create_And_Sql_Insert_Select()
	{
		RequireNative();
		var dir = MakeTempDir();
		try
		{
			using var db = MongrelDBNative.Create(dir, SchemaJson);

			// Insert via SQL.
			db.SqlRows("INSERT INTO users (id, name) VALUES (1, 'alice')");

			// SELECT via SQL.
			var rows = db.SqlRows("SELECT id, name FROM users");
			Assert.Single(rows);
			Assert.Equal(1L, rows[0]["id"]);
			Assert.Equal("alice", rows[0]["name"]);
		}
		finally
		{
			try { Directory.Delete(dir, true); } catch { }
		}
	}

	[SkippableFact]
	public void SqlArrow_Returns_Arrow_Magic()
	{
		RequireNative();
		var dir = MakeTempDir();
		try
		{
			using var db = MongrelDBNative.Create(dir, SchemaJson);
			db.SqlRows("INSERT INTO users (id, name) VALUES (1, 'bob')");

			var arrow = db.SqlArrow("SELECT id FROM users");
			Assert.True(arrow.Length >= 6, "Arrow IPC should be at least 6 bytes");
			Assert.Equal("ARROW1"u8.ToArray(), arrow[..6]);
		}
		finally
		{
			try { Directory.Delete(dir, true); } catch { }
		}
	}

	[SkippableFact]
	public void Migrate_Creates_Table_And_AppliedMigrations_Reads_Back()
	{
		RequireNative();
		var dir = MakeTempDir();
		try
		{
			using var db = MongrelDBNative.Create(dir, SchemaJson);

			var migrations = """
				[{
					"version": 1,
					"name": "add_orders",
					"ops": [{"raw_sql": "CREATE TABLE orders (id INT64 PRIMARY KEY, total FLOAT64)"}]
				}]
				""";
			db.Migrate(migrations);

			// Insert into the migrated table.
			db.SqlRows("INSERT INTO orders (id, total) VALUES (1, 99.99)");

			// Read back applied migrations.
			var applied = db.AppliedMigrationsJson();
			Assert.Contains("add_orders", applied);
		}
		finally
		{
			try { Directory.Delete(dir, true); } catch { }
		}
	}

	[SkippableFact]
	public void QuerySelect_Returns_Rows()
	{
		RequireNative();
		var dir = MakeTempDir();
		try
		{
			using var db = MongrelDBNative.Create(dir, SchemaJson);
			db.SqlRows("INSERT INTO users (id, name) VALUES (1, 'carol')");

			var selectJson = """
				{"table":"users","columns":[],"filter":null,"order_by":[],"limit":null,"offset":null}
				""";
			var rows = db.QuerySelect(selectJson);
			Assert.NotEmpty(rows);
			Assert.True(rows.Any(r => "carol".Equals(r.GetValueOrDefault("name"))));
		}
		finally
		{
			try { Directory.Delete(dir, true); } catch { }
		}
	}

	[SkippableFact]
	public void Error_Handling_Throws_On_Invalid_Sql()
	{
		RequireNative();
		var dir = MakeTempDir();
		try
		{
			using var db = MongrelDBNative.Create(dir, SchemaJson);

			Assert.Throws<QueryException>(() =>
				db.SqlRows("SELECT * FROM nonexistent_table"));
		}
		finally
		{
			try { Directory.Delete(dir, true); } catch { }
		}
	}
}
