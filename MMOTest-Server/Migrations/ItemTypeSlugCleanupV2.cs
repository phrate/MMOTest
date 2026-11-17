using Godot;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server.Migrations;

/// <summary>
/// Fixes the fallout of ItemCapabilitiesV1's silent DROP COLUMN failure.
/// On DBs whose items table still has type_slug NOT NULL (because the
/// previous migration caught and swallowed the failure while
/// idx_items_type still referenced the column), this drops the column —
/// or rebuilds the table on SQLite versions that predate DROP COLUMN.
///
/// Runs after ItemIconPathV1, so description / base_price / is_active /
/// icon_path are guaranteed to exist. FKs are OFF during the whole
/// migration window (Database.Initialize disables them before the schema
/// pass), so the rebuild path doesn't cascade-delete item_tags etc.
/// </summary>
internal static class ItemTypeSlugCleanupV2
{
	private const string MigrationId = "item_type_slug_cleanup_v2";

	public static void Apply(SqliteConnection conn)
	{
		if (MigrationHelpers.TableExists(conn, "schema_migrations"))
		{
			using var check = conn.CreateCommand();
			check.CommandText = "SELECT 1 FROM schema_migrations WHERE migration_id = $id;";
			check.Parameters.AddWithValue("$id", MigrationId);
			if (check.ExecuteScalar() != null) return;
		}

		using var tx = conn.BeginTransaction();

		if (MigrationHelpers.ColumnExists(conn, "items", "type_slug"))
		{
			Exec(conn, tx, "DROP INDEX IF EXISTS idx_items_type;");
			try
			{
				Exec(conn, tx, "ALTER TABLE items DROP COLUMN type_slug;");
				GD.Print("[Server] Migration: dropped items.type_slug");
			}
			catch (SqliteException e)
			{
				GD.Print($"[Server] Migration: rebuilding items (DROP COLUMN failed: {e.Message})");
				RebuildItemsTable(conn, tx);
			}
		}

		using (var mark = conn.CreateCommand())
		{
			mark.Transaction = tx;
			mark.CommandText = "INSERT INTO schema_migrations (migration_id) VALUES ($id);";
			mark.Parameters.AddWithValue("$id", MigrationId);
			mark.ExecuteNonQuery();
		}

		tx.Commit();
		GD.Print("[Server] Migration: item_type_slug_cleanup_v2 applied");
	}

	private static void RebuildItemsTable(SqliteConnection conn, SqliteTransaction tx)
	{
		Exec(conn, tx, @"
			CREATE TABLE items_rebuild (
				id              INTEGER PRIMARY KEY AUTOINCREMENT,
				slug_id         TEXT    NOT NULL UNIQUE,
				display_name    TEXT    NOT NULL,
				type            TEXT    NOT NULL CHECK (type IN ('weapon', 'consumable', 'armor', 'misc')),
				stackable       INTEGER NOT NULL DEFAULT 0 CHECK (stackable IN (0, 1)),
				can_trade       INTEGER NOT NULL DEFAULT 1 CHECK (can_trade IN (0, 1)),
				cert_of_slug    TEXT    NOT NULL DEFAULT '',
				non_certable    INTEGER NOT NULL DEFAULT 0 CHECK (non_certable IN (0, 1)),
				non_bankable    INTEGER NOT NULL DEFAULT 0 CHECK (non_bankable IN (0, 1)),
				description     TEXT    NOT NULL DEFAULT '',
				base_price      INTEGER NOT NULL DEFAULT 0,
				is_active       INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
				icon_path       TEXT    NOT NULL DEFAULT ''
			);");

		Exec(conn, tx, @"
			INSERT INTO items_rebuild
				(id, slug_id, display_name, type, stackable, can_trade,
				 cert_of_slug, non_certable, non_bankable,
				 description, base_price, is_active, icon_path)
			SELECT
				id, slug_id, display_name, type, stackable, can_trade,
				cert_of_slug, non_certable, non_bankable,
				COALESCE(description, ''),
				COALESCE(base_price, 0),
				COALESCE(is_active, 1),
				COALESCE(icon_path, '')
			FROM items;");

		Exec(conn, tx, "DROP TABLE items;");
		Exec(conn, tx, "ALTER TABLE items_rebuild RENAME TO items;");
	}

	private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
	{
		using var cmd = conn.CreateCommand();
		cmd.Transaction = tx;
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}
}