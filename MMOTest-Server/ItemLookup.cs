using System.Collections.Generic;

namespace MMOTest.Server;

/// <summary>
/// Read-only lookup for item definitions. Loaded once at startup from
/// ItemRepository and never mutated. Two indices: by-id (for resolving
/// ItemStack.itemId → metadata) and by-slug (for resolving GFS identifiers
/// like "item_knife" → numeric id).
///
/// Exposes methods rather than dictionaries so callers can't accidentally
/// write to the indices, and so tests can inject a fake without needing
/// a real database.
/// </summary>
public sealed class ItemLookup
{
    private readonly Dictionary<long, ItemMeta> _byId = new();
    private readonly Dictionary<string, long> _idsBySlug = new();

    public int Count => _byId.Count;

    public void Load(IEnumerable<ItemMeta> items)
    {
        _byId.Clear();
        _idsBySlug.Clear();
        foreach (var it in items)
        {
            _byId[it.Id] = it;
            _idsBySlug[it.Slug] = it.Id;
        }
    }

    public ItemMeta? GetById(long id)
        => _byId.TryGetValue(id, out var meta) ? meta : null;

    public long GetIdBySlug(string slug)
        => _idsBySlug.TryGetValue(slug, out var id) ? id : 0L;

    public bool TryGetById(long id, out ItemMeta meta)
        => _byId.TryGetValue(id, out meta!);

    public bool TryGetIdBySlug(string slug, out long id)
        => _idsBySlug.TryGetValue(slug, out id);

    public bool ContainsId(long id) => _byId.ContainsKey(id);
    public bool ContainsSlug(string slug) => _idsBySlug.ContainsKey(slug);
}