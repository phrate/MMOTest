using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MMOTest.Server;

public record ItemMeta(
    long Id, string Slug, string Display, string Type, string TypeSlug,
    bool Stackable, bool CanTrade, string CertOfSlug,
    bool NonCertable, bool NonBankable);

public sealed class ItemRepository
{
    private readonly System.Func<SqliteConnection> _open;
    public ItemRepository(System.Func<SqliteConnection> open) { _open = open; }

    public Task<List<ItemMeta>> GetAllSync() => Task.Run(() =>
    {
        using var conn = _open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, slug_id, display_name, type, type_slug,
                                   stackable, can_trade, cert_of_slug,
                                   non_certable, non_bankable FROM items;";
        using var r = cmd.ExecuteReader();
        var list = new List<ItemMeta>();
        while (r.Read())
            list.Add(new ItemMeta(
                r.GetInt64(0), r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetString(4),
                r.GetInt64(5) != 0, r.GetInt64(6) != 0,
                r.IsDBNull(7) ? "" : r.GetString(7),
                r.GetInt64(8) != 0, r.GetInt64(9) != 0));
        return list;
    });
}