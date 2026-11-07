using Godot;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

public partial class NpcEditorBridge : Node
{
	private string _connString;

	public override void _Ready()
	{
		var path = ProjectSettings.GlobalizePath("user://mmotest.db");
		var b = new SqliteConnectionStringBuilder { DataSource = path };
		_connString = b.ToString();
	}

	private SqliteConnection Open()
	{
		var conn = new SqliteConnection(_connString);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 2000;";
		cmd.ExecuteNonQuery();
		return conn;
	}

	// ============================================================
	// Templates
	// ============================================================

	public Godot.Collections.Array GetTemplates()
	{
		var outArr = new Godot.Collections.Array();
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = @"
			SELECT id, slug_id FROM combat_levels
			WHERE template_slug IS NULL AND slug_id LIKE 'npc\_%' ESCAPE '\'
			ORDER BY slug_id;";
		using var reader = cmd.ExecuteReader();
		while (reader.Read())
			outArr.Add(new Godot.Collections.Dictionary { ["id"] = reader.GetInt64(0), ["slug_id"] = reader.GetString(1) });
		return outArr;
	}

	public Godot.Collections.Dictionary GetTemplate(long id)
	{
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = @"
			SELECT id, slug_id, final_level,
				   hitpoints, max_hitpoints, accuracy, max_accuracy,
				   strength, max_strength, defence, max_defence,
				   ether, max_ether,
				   poisoned, burning, frozen,
				   melee_bonus, ranged_bonus, ether_bonus,
				   melee_defense, ranged_defense, ether_defense,
				   attack_speed
			FROM combat_levels WHERE id = $id;";
		cmd.Parameters.AddWithValue("$id", id);
		using var r = cmd.ExecuteReader();
		if (!r.Read()) return null;
		return new Godot.Collections.Dictionary
		{
			["id"] = r.GetInt64(0),
			["slug_id"] = r.GetString(1),
			["final_level"] = r.GetInt32(2),
			["hitpoints"] = r.GetInt32(3),
			["max_hitpoints"] = r.GetInt32(4),
			["accuracy"] = r.GetInt32(5),
			["max_accuracy"] = r.GetInt32(6),
			["strength"] = r.GetInt32(7),
			["max_strength"] = r.GetInt32(8),
			["defence"] = r.GetInt32(9),
			["max_defence"] = r.GetInt32(10),
			["ether"] = r.GetInt32(11),
			["max_ether"] = r.GetInt32(12),
			["poisoned"] = r.GetInt32(13) != 0,
			["burning"] = r.GetInt32(14) != 0,
			["frozen"] = r.GetInt32(15) != 0,
			["melee_bonus"] = r.GetInt32(16),
			["ranged_bonus"] = r.GetInt32(17),
			["ether_bonus"] = r.GetInt32(18),
			["melee_defense"] = r.GetInt32(19),
			["ranged_defense"] = r.GetInt32(20),
			["ether_defense"] = r.GetInt32(21),
			["attack_speed"] = r.GetInt32(22),
		};
	}

	public bool SaveTemplate(Godot.Collections.Dictionary d)
	{
		try
		{
			using var conn = Open();
			using var cmd = conn.CreateCommand();
			cmd.CommandText = @"
				UPDATE combat_levels SET
					final_level   = $fl,
					hitpoints     = $hp, max_hitpoints = $mhp,
					accuracy      = $acc, max_accuracy  = $mac,
					strength      = $str, max_strength  = $mst,
					defence       = $def, max_defence   = $mdf,
					ether         = $eth, max_ether     = $met,
					melee_bonus   = $mb,  ranged_bonus  = $rb,
					ether_bonus   = $eb,
					melee_defense = $md,  ranged_defense = $rd,
					ether_defense = $ed,
					attack_speed  = $aspeed
				WHERE id = $id;";
			cmd.Parameters.AddWithValue("$fl",     GetInt(d, "final_level", 3));
			cmd.Parameters.AddWithValue("$hp",     GetInt(d, "hitpoints", 10));
			cmd.Parameters.AddWithValue("$mhp",    GetInt(d, "max_hitpoints", 10));
			cmd.Parameters.AddWithValue("$acc",    GetInt(d, "accuracy", 1));
			cmd.Parameters.AddWithValue("$mac",    GetInt(d, "max_accuracy", 1));
			cmd.Parameters.AddWithValue("$str",    GetInt(d, "strength", 1));
			cmd.Parameters.AddWithValue("$mst",    GetInt(d, "max_strength", 1));
			cmd.Parameters.AddWithValue("$def",    GetInt(d, "defence", 1));
			cmd.Parameters.AddWithValue("$mdf",    GetInt(d, "max_defence", 1));
			cmd.Parameters.AddWithValue("$eth",    GetInt(d, "ether", 1));
			cmd.Parameters.AddWithValue("$met",    GetInt(d, "max_ether", 1));
			cmd.Parameters.AddWithValue("$mb",     GetInt(d, "melee_bonus", 0));
			cmd.Parameters.AddWithValue("$rb",     GetInt(d, "ranged_bonus", 0));
			cmd.Parameters.AddWithValue("$eb",     GetInt(d, "ether_bonus", 0));
			cmd.Parameters.AddWithValue("$md",     GetInt(d, "melee_defense", 0));
			cmd.Parameters.AddWithValue("$rd",     GetInt(d, "ranged_defense", 0));
			cmd.Parameters.AddWithValue("$ed",     GetInt(d, "ether_defense", 0));
			cmd.Parameters.AddWithValue("$aspeed", GetInt(d, "attack_speed", 10));
			cmd.Parameters.AddWithValue("$id",     GetLong(d, "id"));
			return cmd.ExecuteNonQuery() > 0;
		}
		catch (System.Exception e) { GD.PrintErr($"[NpcEditorBridge] SaveTemplate: {e.Message}"); return false; }
	}

	// ============================================================
	// Uniques
	// ============================================================

	public Godot.Collections.Array GetNpcs()
	{
		var outArr = new Godot.Collections.Array();
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT id, name FROM npcs ORDER BY name;";
		using var r = cmd.ExecuteReader();
		while (r.Read())
			outArr.Add(new Godot.Collections.Dictionary { ["id"] = r.GetInt64(0), ["name"] = r.GetString(1) });
		return outArr;
	}

	public Godot.Collections.Dictionary GetNpcFull(long id)
	{
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = @"
			SELECT n.id, n.name, n.display, n.tile_x, n.tile_y, n.tile_z,
				   n.playing_animation, n.loot_table_name,
				   n.is_dead, n.is_dying, n.spawn_timer,
				   n.spawn_flags, n.ai_package_state, n.ai_action_state,
				   n.attackable, n.combat_levels_id, n.render_slug, n.dialogue_script,
				   n.aggressive, n.aggro_range,
				   c.slug_id, c.template_slug, c.final_level,
				   c.hitpoints, c.max_hitpoints,
				   c.accuracy, c.max_accuracy, c.strength, c.max_strength,
				   c.defence, c.max_defence, c.ether, c.max_ether,
				   c.poisoned, c.burning, c.frozen,
				   c.melee_bonus, c.ranged_bonus, c.ether_bonus,
				   c.melee_defense, c.ranged_defense, c.ether_defense,
				   c.attack_speed
			FROM npcs n
			LEFT JOIN combat_levels c ON n.combat_levels_id = c.id
			WHERE n.id = $id;";
		cmd.Parameters.AddWithValue("$id", id);
		using var r = cmd.ExecuteReader();
		if (!r.Read()) return null;
		var d = new Godot.Collections.Dictionary
		{
			["id"] = r.GetInt64(0),
			["name"] = r.GetString(1),
			["display"] = r.GetString(2),
			["tile_x"] = (int)r.GetInt64(3),
			["tile_y"] = (int)r.GetInt64(4),
			["tile_z"] = (int)r.GetInt64(5),
			["playing_animation"] = r.GetString(6),
			["loot_table_name"] = r.GetString(7),
			["is_dead"] = r.GetInt64(8) != 0,
			["is_dying"] = r.GetInt64(9) != 0,
			["spawn_timer"] = (int)r.GetInt64(10),
			["spawn_flags"] = r.GetString(11),
			["ai_package_state"] = r.GetString(12),
			["ai_action_state"] = r.GetString(13),
			["attackable"] = r.GetInt64(14) != 0,
			["combat_levels_id"] = r.IsDBNull(15) ? 0L : r.GetInt64(15),
			["render_slug"] = r.IsDBNull(16) ? "" : r.GetString(16),
			["dialogue_script"] = r.IsDBNull(17) ? "" : r.GetString(17),
			["aggressive"] = r.GetInt64(18) != 0,
			["aggro_range"] = r.GetInt32(19),
		};
		if (!r.IsDBNull(19))
		{
			d["combat_slug"] = r.GetString(20);
			d["template_slug"] = r.IsDBNull(21) ? "" : r.GetString(20);
			d["final_level"] = r.GetInt32(22);
			d["hitpoints"] = r.GetInt32(23);
			d["max_hitpoints"] = r.GetInt32(24);
			d["accuracy"] = r.GetInt32(25);
			d["max_accuracy"] = r.GetInt32(26);
			d["strength"] = r.GetInt32(27);
			d["max_strength"] = r.GetInt32(28);
			d["defence"] = r.GetInt32(29);
			d["max_defence"] = r.GetInt32(30);
			d["ether"] = r.GetInt32(31);
			d["max_ether"] = r.GetInt32(32);
			d["poisoned"] = r.GetInt32(33) != 0;
			d["burning"] = r.GetInt32(34) != 0;
			d["frozen"] = r.GetInt32(35) != 0;
			d["melee_bonus"] = r.GetInt32(36);
			d["ranged_bonus"] = r.GetInt32(37);
			d["ether_bonus"] = r.GetInt32(38);
			d["melee_defense"] = r.GetInt32(39);
			d["ranged_defense"] = r.GetInt32(40);
			d["ether_defense"] = r.GetInt32(41);
			d["attack_speed"] = r.GetInt32(42);
		}
		return d;
	}

	public bool SaveNpc(Godot.Collections.Dictionary d)
	{
		try
		{
			using var conn = Open();
			using var tx = conn.BeginTransaction();

			using (var cmd = conn.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = @"
					UPDATE npcs SET
						display = $display, render_slug = $render_slug,
						tile_x = $tx, tile_y = $ty, tile_z = $tz,
						attackable = $attackable,
						aggressive = $aggr, aggro_range = $arange,
						ai_package_state = $ai_pkg, ai_action_state = $ai_act,
						spawn_flags = $sf, loot_table_name = $lt,
						spawn_timer = $stimer,
						dialogue_script = $dialogue_script
					WHERE id = $id;";
				cmd.Parameters.AddWithValue("$display", GetString(d, "display", ""));
				cmd.Parameters.AddWithValue("$render_slug", GetString(d, "render_slug", ""));
				cmd.Parameters.AddWithValue("$tx", GetInt(d, "tile_x", 0));
				cmd.Parameters.AddWithValue("$ty", GetInt(d, "tile_y", 0));
				cmd.Parameters.AddWithValue("$tz", GetInt(d, "tile_z", 100));
				cmd.Parameters.AddWithValue("$attackable", GetBool(d, "attackable", true) ? 1 : 0);
				cmd.Parameters.AddWithValue("$aggr", GetBool(d, "aggressive", false) ? 1 : 0);
				cmd.Parameters.AddWithValue("$arange", GetInt(d, "aggro_range", 8));
				cmd.Parameters.AddWithValue("$ai_pkg", GetString(d, "ai_package_state", "idle"));
				cmd.Parameters.AddWithValue("$ai_act", GetString(d, "ai_action_state", "none"));
				cmd.Parameters.AddWithValue("$sf", GetString(d, "spawn_flags", "{}"));
				cmd.Parameters.AddWithValue("$lt", GetString(d, "loot_table_name", "common_loot_1"));
				cmd.Parameters.AddWithValue("$stimer", GetInt(d, "spawn_timer", 30));
				cmd.Parameters.AddWithValue("$id", GetLong(d, "id"));
				cmd.ExecuteNonQuery();
			}

			long clId = GetLong(d, "combat_levels_id");
			if (clId > 0)
			{
				using var cmd = conn.CreateCommand();
				cmd.Transaction = tx;
				cmd.CommandText = @"
					UPDATE combat_levels SET
						final_level = $fl,
						hitpoints = $hp, max_hitpoints = $mhp,
						accuracy = $ac, max_accuracy = $mac,
						strength = $st, max_strength = $mst,
						defence = $df, max_defence = $mdf,
						ether = $eth, max_ether = $meth,
						poisoned = $pois, burning = $burn, frozen = $froze,
						melee_bonus = $mb, ranged_bonus = $rb, ether_bonus = $eb,
						melee_defense = $md, ranged_defense = $rd, ether_defense = $ed,
						attack_speed = $aspeed
					WHERE id = $id;";
				cmd.Parameters.AddWithValue("$fl",     GetInt(d, "final_level", 3));
				cmd.Parameters.AddWithValue("$hp",     GetInt(d, "hitpoints", 3));
				cmd.Parameters.AddWithValue("$mhp",    GetInt(d, "max_hitpoints", 3));
				cmd.Parameters.AddWithValue("$ac",     GetInt(d, "accuracy", 1));
				cmd.Parameters.AddWithValue("$mac",    GetInt(d, "max_accuracy", 1));
				cmd.Parameters.AddWithValue("$st",     GetInt(d, "strength", 1));
				cmd.Parameters.AddWithValue("$mst",    GetInt(d, "max_strength", 1));
				cmd.Parameters.AddWithValue("$df",     GetInt(d, "defence", 1));
				cmd.Parameters.AddWithValue("$mdf",    GetInt(d, "max_defence", 1));
				cmd.Parameters.AddWithValue("$eth",    GetInt(d, "ether", 1));
				cmd.Parameters.AddWithValue("$meth",   GetInt(d, "max_ether", 1));
				cmd.Parameters.AddWithValue("$pois",   GetBool(d, "poisoned", false) ? 1 : 0);
				cmd.Parameters.AddWithValue("$burn",   GetBool(d, "burning", false) ? 1 : 0);
				cmd.Parameters.AddWithValue("$froze",  GetBool(d, "frozen", false) ? 1 : 0);
				cmd.Parameters.AddWithValue("$mb",     GetInt(d, "melee_bonus", 0));
				cmd.Parameters.AddWithValue("$rb",     GetInt(d, "ranged_bonus", 0));
				cmd.Parameters.AddWithValue("$eb",     GetInt(d, "ether_bonus", 0));
				cmd.Parameters.AddWithValue("$md",     GetInt(d, "melee_defense", 0));
				cmd.Parameters.AddWithValue("$rd",     GetInt(d, "ranged_defense", 0));
				cmd.Parameters.AddWithValue("$ed",     GetInt(d, "ether_defense", 0));
				cmd.Parameters.AddWithValue("$aspeed", GetInt(d, "attack_speed", 10));
				cmd.Parameters.AddWithValue("$id",     clId);
				cmd.ExecuteNonQuery();
			}

			tx.Commit();
			return true;
		}
		catch (System.Exception e) { GD.PrintErr($"[NpcEditorBridge] SaveNpc: {e.Message}"); return false; }
	}

	// ============================================================
	// Helpers
	// ============================================================

	private static long GetLong(Godot.Collections.Dictionary d, string k, long fb = 0) => d.ContainsKey(k) ? d[k].AsInt64() : fb;
	private static int GetInt(Godot.Collections.Dictionary d, string k, int fb = 0) => d.ContainsKey(k) ? d[k].AsInt32() : fb;
	private static string GetString(Godot.Collections.Dictionary d, string k, string fb = "") => d.ContainsKey(k) ? d[k].AsString() : fb;
	private static bool GetBool(Godot.Collections.Dictionary d, string k, bool fb = false) => d.ContainsKey(k) ? d[k].AsBool() : fb;
}
