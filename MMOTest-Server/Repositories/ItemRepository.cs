using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

public record EquipmentCap(
    string Slot, int AttackSpeedOverride,
    int AttackMelee, int AttackRanged, int AttackEther,
    int DefenseMelee, int DefenseRanged, int DefenseEther,
    int StrengthMelee, int StrengthRanged, int StrengthEther);

public record ConsumableCap(string EffectType, int HealAmount);

public record ItemMeta(
    long Id, string Slug, string Display, string Type,
    bool Stackable, bool CanTrade, string CertOfSlug,
    bool NonCertable, bool NonBankable, bool IsActive,
    int BasePrice, string Description, string IconPath,
    IReadOnlyList<string> Tags,
    EquipmentCap? Equipment,
    ConsumableCap? Consumable,
    string? SetSlug);

public sealed class ItemRepository
{
    private readonly Func<SqliteConnection> _open;
    public ItemRepository(Func<SqliteConnection> open) { _open = open; }

    private const string SelectColumns = @"
        i.id, i.slug_id, i.display_name, i.type,
        i.stackable, i.can_trade, i.cert_of_slug,
        i.non_certable, i.non_bankable, i.is_active,
        i.base_price, i.description, i.icon_path,
        GROUP_CONCAT(DISTINCT t.tag) AS tags_csv,
        e.slot, e.attack_speed_override,
        e.attack_bonus_melee, e.attack_bonus_ranged, e.attack_bonus_ether,
        e.defense_bonus_melee, e.defense_bonus_ranged, e.defense_bonus_ether,
        e.bonus_melee_strength, e.bonus_ranged_strength, e.bonus_ether_strength,
        c.effect_type, c.heal_amount,
        sm.set_slug";

    private const string FromJoins = @"
        FROM items i
        LEFT JOIN item_tags t ON t.item_id = i.id
        LEFT JOIN item_equipment e ON e.item_id = i.id
        LEFT JOIN item_consumable c ON c.item_id = i.id
        LEFT JOIN item_set_member sm ON sm.item_id = i.id";

    public Task<List<ItemMeta>> GetAllSync() => Task.Run(() => LoadItems(whereClause: ""));

    public List<ItemMeta> GetForEditorSync(bool includeInactive)
        => LoadItems(whereClause: includeInactive
            ? "WHERE i.cert_of_slug = ''"
            : "WHERE i.cert_of_slug = '' AND i.is_active = 1");

    public ItemMeta? GetByIdSync(long id)
    {
        var list = LoadItems(whereClause: "WHERE i.id = $id",
                             parameters: new Dictionary<string, object> { ["$id"] = id });
        return list.Count > 0 ? list[0] : null;
    }

    public List<string> GetAllTagsSync()
    {
        var result = new List<string>();
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT tag FROM item_tags ORDER BY tag;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(r.GetString(0));
        return result;
    }

	public List<(long Id, string Slug, string Display)> SearchSync(string search, int limit)
	{
		var result = new List<(long, string, string)>();
		if (limit <= 0) limit = 20;
		using var conn = _open();
		using var cmd = conn.CreateCommand();
		bool hasSearch = !string.IsNullOrWhiteSpace(search);
		cmd.CommandText = hasSearch
			? @"SELECT id, slug_id, display_name FROM items
				WHERE is_active = 1 AND (slug_id LIKE $s COLLATE NOCASE OR display_name LIKE $s COLLATE NOCASE)
				ORDER BY slug_id COLLATE NOCASE LIMIT $limit;"
			: @"SELECT id, slug_id, display_name FROM items
				WHERE is_active = 1
				ORDER BY slug_id COLLATE NOCASE LIMIT $limit;";
		if (hasSearch) cmd.Parameters.AddWithValue("$s", "%" + search + "%");
		cmd.Parameters.AddWithValue("$limit", limit);
		using var r = cmd.ExecuteReader();
		while (r.Read())
			result.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
		return result;
	}

    private List<ItemMeta> LoadItems(string whereClause, Dictionary<string, object>? parameters = null)
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} {FromJoins} {whereClause} GROUP BY i.id ORDER BY i.slug_id COLLATE NOCASE;";
        if (parameters != null)
            foreach (var kv in parameters) cmd.Parameters.AddWithValue(kv.Key, kv.Value);

        var list = new List<ItemMeta>();
        using var r = cmd.ExecuteReader();

        int c_id = r.GetOrdinal("id");
        int c_slug = r.GetOrdinal("slug_id");
        int c_disp = r.GetOrdinal("display_name");
        int c_type = r.GetOrdinal("type");
        int c_stk = r.GetOrdinal("stackable");
        int c_trd = r.GetOrdinal("can_trade");
        int c_cert = r.GetOrdinal("cert_of_slug");
        int c_ncrt = r.GetOrdinal("non_certable");
        int c_nbnk = r.GetOrdinal("non_bankable");
        int c_act = r.GetOrdinal("is_active");
        int c_price = r.GetOrdinal("base_price");
        int c_desc = r.GetOrdinal("description");
        int c_icon = r.GetOrdinal("icon_path");
        int c_tags = r.GetOrdinal("tags_csv");
        int c_eq_slot = r.GetOrdinal("slot");
        int c_eq_speed = r.GetOrdinal("attack_speed_override");
        int c_eq_am = r.GetOrdinal("attack_bonus_melee");
        int c_eq_ar = r.GetOrdinal("attack_bonus_ranged");
        int c_eq_ae = r.GetOrdinal("attack_bonus_ether");
        int c_eq_dm = r.GetOrdinal("defense_bonus_melee");
        int c_eq_dr = r.GetOrdinal("defense_bonus_ranged");
        int c_eq_de = r.GetOrdinal("defense_bonus_ether");
        int c_eq_sm = r.GetOrdinal("bonus_melee_strength");
        int c_eq_sr = r.GetOrdinal("bonus_ranged_strength");
        int c_eq_se = r.GetOrdinal("bonus_ether_strength");
        int c_cs_effect = r.GetOrdinal("effect_type");
        int c_cs_heal = r.GetOrdinal("heal_amount");
        int c_set = r.GetOrdinal("set_slug");

        while (r.Read())
        {
            var tags = r.IsDBNull(c_tags)
                ? new List<string>()
                : r.GetString(c_tags).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

            EquipmentCap? eq = null;
            if (!r.IsDBNull(c_eq_slot))
            {
                eq = new EquipmentCap(
                    r.GetString(c_eq_slot),
                    r.GetInt32(c_eq_speed),
                    r.GetInt32(c_eq_am), r.GetInt32(c_eq_ar), r.GetInt32(c_eq_ae),
                    r.GetInt32(c_eq_dm), r.GetInt32(c_eq_dr), r.GetInt32(c_eq_de),
                    r.GetInt32(c_eq_sm), r.GetInt32(c_eq_sr), r.GetInt32(c_eq_se));
            }

            ConsumableCap? cons = null;
            if (!r.IsDBNull(c_cs_effect))
                cons = new ConsumableCap(r.GetString(c_cs_effect), r.GetInt32(c_cs_heal));

            string? setSlug = r.IsDBNull(c_set) ? null : r.GetString(c_set);

            list.Add(new ItemMeta(
                r.GetInt64(c_id),
                r.GetString(c_slug),
                r.GetString(c_disp),
                r.GetString(c_type),
                r.GetInt64(c_stk) != 0,
                r.GetInt64(c_trd) != 0,
                r.IsDBNull(c_cert) ? "" : r.GetString(c_cert),
                r.GetInt64(c_ncrt) != 0,
                r.GetInt64(c_nbnk) != 0,
                r.GetInt64(c_act) != 0,
                r.GetInt32(c_price),
                r.GetString(c_desc),
                r.GetString(c_icon),
                tags,
                eq,
                cons,
                setSlug));
        }
        return list;
    }

    public bool SlugExistsSync(string slug)
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM items WHERE slug_id = $s;";
        cmd.Parameters.AddWithValue("$s", slug);
        return cmd.ExecuteScalar() != null;
    }

    public Task<long> CreateAsync(Dictionary<string, object> u) => Task.Run(() =>
    {
        using var conn = _open();
        using var tx = conn.BeginTransaction();
        try
        {
            long itemId;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO items
                        (slug_id, display_name, type, stackable, can_trade,
                         cert_of_slug, non_certable, non_bankable,
                         description, icon_path, base_price, is_active)
                    VALUES ($slug, $disp, $type, $stk, $trd,
                            '', $ncrt, $nbnk, $desc, $icon, $price, 1)
                    RETURNING id;";
                cmd.Parameters.AddWithValue("$slug",  GetString(u, "slug_id", ""));
                cmd.Parameters.AddWithValue("$disp",  GetString(u, "display", ""));
                cmd.Parameters.AddWithValue("$type",  GetString(u, "type", "misc"));
                cmd.Parameters.AddWithValue("$stk",   GetBool(u, "stackable", false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$trd",   GetBool(u, "can_trade", true) ? 1 : 0);
                cmd.Parameters.AddWithValue("$ncrt",  GetBool(u, "non_certable", false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$nbnk",  GetBool(u, "non_bankable", false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$desc",  GetString(u, "description", ""));
                cmd.Parameters.AddWithValue("$icon",  GetString(u, "icon_path", ""));
                cmd.Parameters.AddWithValue("$price", GetInt(u, "base_price", 0));
                itemId = (long)cmd.ExecuteScalar()!;
            }

            InsertCapabilities(conn, tx, itemId, u);
            tx.Commit();
            return itemId;
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    });

    public Task<bool> UpdateAsync(long itemId, Dictionary<string, object> u) => Task.Run(() =>
    {
        using var conn = _open();
        using var tx = conn.BeginTransaction();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE items SET
                        display_name    = $disp,
                        type            = $type,
                        stackable       = $stk,
                        can_trade       = $trd,
                        non_certable    = $ncrt,
                        non_bankable    = $nbnk,
                        description     = $desc,
                        icon_path       = $icon,
                        base_price      = $price,
                        is_active       = $active
                    WHERE id = $id;";
                cmd.Parameters.AddWithValue("$disp",   GetString(u, "display", ""));
                cmd.Parameters.AddWithValue("$type",   GetString(u, "type", "misc"));
                cmd.Parameters.AddWithValue("$stk",    GetBool(u, "stackable", false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$trd",    GetBool(u, "can_trade", true) ? 1 : 0);
                cmd.Parameters.AddWithValue("$ncrt",   GetBool(u, "non_certable", false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$nbnk",   GetBool(u, "non_bankable", false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$desc",   GetString(u, "description", ""));
                cmd.Parameters.AddWithValue("$icon",   GetString(u, "icon_path", ""));
                cmd.Parameters.AddWithValue("$price",  GetInt(u, "base_price", 0));
                cmd.Parameters.AddWithValue("$active", GetBool(u, "is_active", true) ? 1 : 0);
                cmd.Parameters.AddWithValue("$id",     itemId);
                if (cmd.ExecuteNonQuery() == 0) { tx.Rollback(); return false; }
            }

            // Clear and re-insert all capabilities
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    DELETE FROM item_tags WHERE item_id = $id;
                    DELETE FROM item_equipment WHERE item_id = $id;
                    DELETE FROM item_consumable WHERE item_id = $id;";
                cmd.Parameters.AddWithValue("$id", itemId);
                cmd.ExecuteNonQuery();
            }
            InsertCapabilities(conn, tx, itemId, u);

            tx.Commit();
            return true;
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
        }
    });

    public Task<bool> SoftDeleteAsync(long itemId) => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET is_active = 0 WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", itemId);
        return cmd.ExecuteNonQuery() > 0;
    });

    private static void InsertCapabilities(SqliteConnection conn, SqliteTransaction tx,
        long itemId, Dictionary<string, object> u)
    {
        if (u.TryGetValue("tags", out var tagsObj) && tagsObj is IEnumerable<string> tags)
        {
            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO item_tags (item_id, tag) VALUES ($id, $tag);";
                cmd.Parameters.AddWithValue("$id", itemId);
                cmd.Parameters.AddWithValue("$tag", tag);
                cmd.ExecuteNonQuery();
            }
        }

        if (u.TryGetValue("equipment", out var eqObj) && eqObj is Dictionary<string, object> eq && eq.Count > 0)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO item_equipment
                    (item_id, slot, attack_speed_override,
                     attack_bonus_melee, attack_bonus_ranged, attack_bonus_ether,
                     defense_bonus_melee, defense_bonus_ranged, defense_bonus_ether,
                     bonus_melee_strength, bonus_ranged_strength, bonus_ether_strength)
                VALUES ($id, $slot, $speed, $am, $ar, $ae, $dm, $dr, $de, $sm, $sr, $se);";
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.Parameters.AddWithValue("$slot",  GetString(eq, "slot", "head"));
            cmd.Parameters.AddWithValue("$speed", GetInt(eq, "attack_speed_override", 0));
            cmd.Parameters.AddWithValue("$am",    GetInt(eq, "attack_bonus_melee", 0));
            cmd.Parameters.AddWithValue("$ar",    GetInt(eq, "attack_bonus_ranged", 0));
            cmd.Parameters.AddWithValue("$ae",    GetInt(eq, "attack_bonus_ether", 0));
            cmd.Parameters.AddWithValue("$dm",    GetInt(eq, "defense_bonus_melee", 0));
            cmd.Parameters.AddWithValue("$dr",    GetInt(eq, "defense_bonus_ranged", 0));
            cmd.Parameters.AddWithValue("$de",    GetInt(eq, "defense_bonus_ether", 0));
            cmd.Parameters.AddWithValue("$sm",    GetInt(eq, "bonus_melee_strength", 0));
            cmd.Parameters.AddWithValue("$sr",    GetInt(eq, "bonus_ranged_strength", 0));
            cmd.Parameters.AddWithValue("$se",    GetInt(eq, "bonus_ether_strength", 0));
            cmd.ExecuteNonQuery();
        }

        if (u.TryGetValue("consumable", out var csObj) && csObj is Dictionary<string, object> cs && cs.Count > 0)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO item_consumable (item_id, effect_type, heal_amount)
                VALUES ($id, $effect, $heal);";
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.Parameters.AddWithValue("$effect", GetString(cs, "effect_type", "utility"));
            cmd.Parameters.AddWithValue("$heal",   GetInt(cs, "heal_amount", 0));
            cmd.ExecuteNonQuery();
        }
    }

    private static int GetInt(Dictionary<string, object> u, string key, int fallback)
    {
        if (!u.TryGetValue(key, out var v) || v == null) return fallback;
        try { return Convert.ToInt32(v); } catch { return fallback; }
    }

    private static bool GetBool(Dictionary<string, object> u, string key, bool fallback)
    {
        if (!u.TryGetValue(key, out var v) || v == null) return fallback;
        try { return Convert.ToBoolean(v); } catch { return fallback; }
    }

    private static string GetString(Dictionary<string, object> u, string key, string fallback)
    {
        if (!u.TryGetValue(key, out var v) || v == null) return fallback;
        return v.ToString() ?? fallback;
    }
}