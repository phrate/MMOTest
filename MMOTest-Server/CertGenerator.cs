using Godot;
using Microsoft.Data.Sqlite;
namespace MMOTest.Server;

internal static class CertGenerator
{
	/// <summary>
	/// For every non-stackable base item with no cert twin, insert a stackable
	/// cert version. Idempotent — safe to run on every startup. Cert rows inherit
	/// the base item's type and can_trade.
	///
	/// Does NOT touch type_slug — that column was retired by
	/// ItemCapabilitiesV1 / ItemTypeSlugCleanupV2.
	/// </summary>
	public static void Ensure(SqliteConnection conn)
	{
		var toCreate = new System.Collections.Generic.List<(long Id, string Slug, string Display, string Type, bool CanTrade)>();
		using (var cmd = conn.CreateCommand())
		{
			cmd.CommandText = @"
				SELECT i.id, i.slug_id, i.display_name, i.type, i.can_trade
				FROM items i
				WHERE i.stackable = 0
				  AND i.cert_of_slug = ''
				  AND i.non_certable = 0
				  AND NOT EXISTS (
					SELECT 1 FROM items c WHERE c.cert_of_slug = i.slug_id
				  );";
			using var r = cmd.ExecuteReader();
			while (r.Read())
				toCreate.Add((r.GetInt64(0), r.GetString(1), r.GetString(2),
							  r.GetString(3), r.GetInt64(4) != 0));
		}
		foreach (var item in toCreate)
		{
			using var ins = conn.CreateCommand();
			ins.CommandText = @"
				INSERT INTO items
					(slug_id, display_name, type, stackable, can_trade, cert_of_slug)
				VALUES
					($slug, $disp, $type, 1, $trade, $cert_of);";
			ins.Parameters.AddWithValue("$slug",    item.Slug + "_cert");
			ins.Parameters.AddWithValue("$disp",    item.Display);
			ins.Parameters.AddWithValue("$type",    item.Type);
			ins.Parameters.AddWithValue("$trade",   item.CanTrade ? 1 : 0);
			ins.Parameters.AddWithValue("$cert_of", item.Slug);
			try { ins.ExecuteNonQuery(); }
			catch (SqliteException) { /* already exists, ignore */ }
		}
		if (toCreate.Count > 0)
			GD.Print($"[Server] Auto-generated {toCreate.Count} cert item(s)");
	}
}