using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// Owns item-combination recipes: pair lookup, GFS script execution against
/// an inventory sandbox, XP awards.
///
/// Coupling note (temporary): holds Main for Send / ChatToPlayer /
/// BroadcastInventory / SendSkills / RouteChatEffects. Phase 4's
/// OutboundChannel absorbs these.
/// </summary>
internal sealed class RecipeSystem
{
    private sealed class RecipeIntent
    {
        public int SlotA { get; init; }
        public int SlotB { get; init; }
    }

    private readonly ServerState _state;
    private readonly ItemLookup _items;
    private readonly RecipeRepository _recipes;
    private readonly InventoryRepository _inventory;
    private readonly PlayerStatsRepository _stats;
    private readonly Gfs.GfsManager _gfs;
    private readonly Gfs.FunctionRegistry _gfsFns;
    private readonly WorldService _world;
    private readonly OutboundChannel _outbound;

    private readonly Dictionary<string, RecipeRecord> _recipesByPair = new();
    private readonly Dictionary<long, List<RecipeIntent>> _pendingRecipeIntents = new();

    public RecipeSystem(ServerState state, ItemLookup items,
                        RecipeRepository recipes, InventoryRepository inventory,
                        PlayerStatsRepository stats, Gfs.GfsManager gfs,
                        Gfs.FunctionRegistry gfsFns, WorldService world,
                        OutboundChannel outbound)
    {
        _state = state;
        _items = items;
        _recipes = recipes;
        _inventory = inventory;
        _stats = stats;
        _gfs = gfs;
        _gfsFns = gfsFns;
        _world = world;
        _outbound = outbound;
    }

    internal void LoadRecipes()
    {
        var recipes = _recipes.GetAllSync();
        _recipesByPair.Clear();
        foreach (var r in recipes)
            _recipesByPair[r.Pair] = r;
        GD.Print($"[Server] Loaded {recipes.Count} recipe(s)");
    }

    internal void HandleCombineAttempt(long peerId, JsonElement root)
    {
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return;
        if (!_state.Inventories.ContainsKey(session.PlayerId)) return;

        if (!root.TryGetProperty("slot_a", out var aEl) || !aEl.TryGetInt32(out int slotA)) return;
        if (!root.TryGetProperty("slot_b", out var bEl) || !bEl.TryGetInt32(out int slotB)) return;
        if (slotA == slotB) return;
        if (slotA < 0 || slotA >= 28 || slotB < 0 || slotB >= 28) return;

        if (!_pendingRecipeIntents.TryGetValue(session.PlayerId, out var list))
        {
            list = new List<RecipeIntent>();
            _pendingRecipeIntents[session.PlayerId] = list;
        }
        list.Add(new RecipeIntent { SlotA = slotA, SlotB = slotB });
    }

    internal void ClearPendingFor(long playerId) => _pendingRecipeIntents.Remove(playerId);

    internal void ProcessRecipeIntents()
    {
        if (_pendingRecipeIntents.Count == 0) return;

        foreach (var playerId in _pendingRecipeIntents.Keys.ToList())
        {
            if (!_pendingRecipeIntents.TryGetValue(playerId, out var intents)) continue;
            _pendingRecipeIntents.Remove(playerId);

            if (!_state.Inventories.TryGetValue(playerId, out var inv)) continue;

            foreach (var intent in intents)
            {
                if (intent.SlotA < 0 || intent.SlotA >= inv.Length) continue;
                if (intent.SlotB < 0 || intent.SlotB >= inv.Length) continue;

                long packedA = inv[intent.SlotA];
                long packedB = inv[intent.SlotB];
                if (packedA == 0 || packedB == 0) continue;

                int itemA = ItemStack.ItemId(packedA);
                int itemB = ItemStack.ItemId(packedB);
                if (itemA <= 0 || itemB <= 0) continue;

                ApplyRecipe(playerId, intent.SlotA, intent.SlotB, itemA, itemB);
            }
        }
    }

    private bool ApplyRecipe(long playerId, int slotA, int slotB, int itemA, int itemB)
    {
        int lowId = Math.Min(itemA, itemB);
        int highId = Math.Max(itemA, itemB);
        string pair = $"{lowId}-{highId}";

        if (!_recipesByPair.TryGetValue(pair, out var recipe))
        {
            _outbound.ChatToPlayer(playerId, "Nothing seemed to happen.");
            return false;
        }

        Diag.Log("diag_recipe", $"[Recipe] attempt: player={playerId} pair={pair} slotA={slotA} slotB={slotB}");

        var ast = _gfs.GetAst(recipe.ScriptPath);
        if (ast == null)
        {
            Diag.Log("diag_recipe", $"[Recipe] no recipe for pair {pair}");
            _outbound.ChatToPlayer(playerId, "Nothing seemed to happen.");
            return false;
        }
        if (ast.Namespace != "server" || ast.Section != "recipe" || ast.Recipe == null)
        {
            GD.PrintErr($"[Recipe] Script {recipe.ScriptPath} is not a [server][recipe] file");
            _outbound.ChatToPlayer(playerId, "Nothing seemed to happen.");
            return false;
        }

        if (!_state.Inventories.TryGetValue(playerId, out var realInv)) return false;
        if (!_state.PeerByPlayerId.TryGetValue(playerId, out var peerId)) return false;
        if (!_state.Sessions.TryGetValue(peerId, out var session)) return false;

        var sandbox = (long[])realInv.Clone();

        var system = new Dictionary<string, object>
        {
            ["SELF_ID"] = playerId,
            ["username"] = session.Username,
            ["slot_a"] = (long)slotA,
            ["slot_b"] = (long)slotB,
            ["item_a"] = (long)itemA,
            ["item_b"] = (long)itemB,
        };

        var ctx = new Gfs.CallContext
        {
            System = system,
            Locals = new Dictionary<string, object>(),
            Args = new Dictionary<string, object>(),
            SelfPlayerId = playerId,
            ScriptPath = recipe.ScriptPath,
            TaskLookup = _gfs.GetTask,
        };

        ctx.ResolveIdent = (name) =>
        {
            if (name.StartsWith("item_"))
                return _items.TryGetIdBySlug(name, out var id) ? (long)id : null;
            if (name.StartsWith("level_"))
            {
                string skillId = name.Substring("level_".Length);
                if (_state.SkillsByPlayer.TryGetValue(playerId, out var skills))
                {
                    var skill = skills.FirstOrDefault(s => s.Id == skillId);
                    if (skill != null) return (long)skill.Max;
                }
                return null;
            }
            GD.PrintErr($"[Recipe] Unresolved identifier '{name}' in {recipe.ScriptPath}");
            return null;
        };

        ctx.ResolveItem = (name) =>
        {
            var stripped = name.StartsWith("item_") ? name.Substring("item_".Length) : name;
            return _items.TryGetIdBySlug(stripped, out var id) ? (long)id : null;
        };

        ctx.CountItemInInventory = (itemId) => CountItemInSandbox(sandbox, itemId);

        try
        {
            Gfs.Interpreter.ExecuteRecipe(ast, ctx, _gfsFns);
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Recipe] Runtime error in {recipe.ScriptPath}: {e.Message}");
            _outbound.ChatToPlayer(playerId, "Nothing seemed to happen.");
            return false;
        }

        int cx = 0, cy = 0;
        if (_state.Positions.TryGetValue(playerId, out var pos))
        {
            int tx = (int)MathF.Round(pos.X);
            int ty = (int)MathF.Round(pos.Z);
            cx = (int)MathF.Floor((float)tx / _world.ChunkSize);
            cy = (int)MathF.Floor((float)ty / _world.ChunkSize);
        }

        if (ctx.RecipeFailed)
        {
            bool changed = false;

            foreach (var (itemId, qty) in ctx.RecipeConsume)
            {
                int removed = RemoveItemFromSandbox(sandbox, itemId, qty);
                if (removed < qty)
                    GD.Print($"[Recipe] {recipe.Slug} failed: requested {qty}x item {itemId}, removed {removed}");
                if (removed > 0) changed = true;
            }

            foreach (var (itemId, qty) in ctx.RecipeOutput)
            {
                if (!_items.TryGetById(itemId, out var meta))
                {
                    GD.PrintErr($"[Recipe] {recipe.Slug} failed: unknown output item {itemId}");
                    continue;
                }
                if (!TryGiveToSandbox(sandbox, itemId, meta.Stackable, qty))
                {
                    GD.Print($"[Recipe] {recipe.Slug} failed: couldn't fit {qty}x item {itemId}");
                    continue;
                }
                changed = true;
            }

            if (changed)
            {
                Array.Copy(sandbox, realInv, realInv.Length);
                for (int i = 0; i < realInv.Length; i++)
                    _inventory.SaveSlotSync(playerId, i, realInv[i]);
                _outbound.BroadcastInventory(playerId);
            }

            if (ctx.Effects.Count > 0)
                _outbound.RouteChatEffects(playerId, session.Username, cx, cy, ctx.Effects, peerId);
            else if (!changed)
                _outbound.ChatToPlayer(playerId, "Nothing seemed to happen.");

            Diag.Log("diag_recipe",
                $"[Recipe] {recipe.Slug} FAILED: consume={ctx.RecipeConsume.Count} output={ctx.RecipeOutput.Count} effects={ctx.Effects.Count} changed={changed}");

            return changed;
        }

        foreach (var (itemId, qty) in ctx.RecipeConsume)
        {
            int removed = RemoveItemFromSandbox(sandbox, itemId, qty);
            if (removed < qty)
            {
                GD.PrintErr($"[Recipe] Failed to consume {qty}x item {itemId} in {recipe.ScriptPath} (removed {removed})");
                _outbound.ChatToPlayer(playerId, "Nothing seemed to happen.");
                return false;
            }
        }

        foreach (var (itemId, qty) in ctx.RecipeOutput)
        {
            if (!_items.TryGetById(itemId, out var meta))
            {
                GD.PrintErr($"[Recipe] Unknown output item {itemId} in {recipe.ScriptPath}");
                _outbound.ChatToPlayer(playerId, "Nothing seemed to happen.");
                return false;
            }
            if (!TryGiveToSandbox(sandbox, itemId, meta.Stackable, qty))
            {
                _outbound.ChatToPlayer(playerId, "You don't have enough inventory space.");
                return false;
            }
        }

        Array.Copy(sandbox, realInv, realInv.Length);
        for (int i = 0; i < realInv.Length; i++)
            _inventory.SaveSlotSync(playerId, i, realInv[i]);
        _outbound.BroadcastInventory(playerId);

        AwardRecipeXp(playerId, recipe.XpRewards);

        if (ctx.Effects.Count > 0)
            _outbound.RouteChatEffects(playerId, session.Username, cx, cy, ctx.Effects, peerId);

        Diag.Log("diag_recipe",
            $"[Recipe] {recipe.Slug} SUCCESS: consume={ctx.RecipeConsume.Count} output={ctx.RecipeOutput.Count} effects={ctx.Effects.Count}");

        return true;
    }

    private static int CountItemInSandbox(long[] sandbox, int itemId)
    {
        int total = 0;
        foreach (long packed in sandbox)
        {
            if (packed == 0) continue;
            if (ItemStack.ItemId(packed) == itemId) total += ItemStack.Quantity(packed);
        }
        return total;
    }

    private static int RemoveItemFromSandbox(long[] sandbox, int itemId, int qty)
    {
        int remaining = qty;
        for (int i = 0; i < sandbox.Length && remaining > 0; i++)
        {
            if (sandbox[i] == 0) continue;
            if (ItemStack.ItemId(sandbox[i]) != itemId) continue;
            int have = ItemStack.Quantity(sandbox[i]);
            int take = Math.Min(have, remaining);
            int left = have - take;
            sandbox[i] = left > 0 ? ItemStack.Encode(itemId, left) : 0;
            remaining -= take;
        }
        return qty - remaining;
    }

    private static bool TryGiveToSandbox(long[] sandbox, int itemId, bool stackable, int qty)
    {
        int remaining = qty;
        if (stackable)
        {
            for (int i = 0; i < sandbox.Length && remaining > 0; i++)
            {
                if (sandbox[i] == 0) continue;
                if (ItemStack.ItemId(sandbox[i]) != itemId) continue;
                sandbox[i] = ItemStack.Encode(itemId, ItemStack.Quantity(sandbox[i]) + remaining);
                remaining = 0;
            }
            for (int i = 0; i < sandbox.Length && remaining > 0; i++)
            {
                if (sandbox[i] != 0) continue;
                sandbox[i] = ItemStack.Encode(itemId, remaining);
                remaining = 0;
            }
        }
        else
        {
            for (int i = 0; i < sandbox.Length && remaining > 0; i++)
            {
                if (sandbox[i] != 0) continue;
                sandbox[i] = ItemStack.Encode(itemId, 1);
                remaining--;
            }
        }
        return remaining == 0;
    }

    private void AwardRecipeXp(long playerId, Dictionary<string, int> xpRewards)
    {
        if (xpRewards.Count == 0) return;
        if (!_state.SkillsByPlayer.TryGetValue(playerId, out var skills)) return;
        if (!_state.PeerByPlayerId.TryGetValue(playerId, out var peerId)) return;

        foreach (var (skillId, rawXp) in xpRewards)
        {
            if (rawXp <= 0) continue;
            int idx = skills.FindIndex(s => s.Id == skillId);
            if (idx < 0)
            {
                GD.PrintErr($"[Recipe] Unknown skill '{skillId}' in xp_rewards");
                continue;
            }

            long tenthsToAdd = (long)rawXp * SkillExperience.XpPrecision;
            var current = skills[idx];
            long newXp = current.CurrentXpTenths + tenthsToAdd;
            int newLevel = Math.Max(current.Max, SkillExperience.GetLevelForXpTenths(newXp));

            skills[idx] = current with
            {
                CurrentXpTenths = newXp,
                Max = newLevel,
            };

            _ = _stats.AwardSkillXpAsync(playerId, skillId, tenthsToAdd, newLevel);
        }

        _outbound.SendSkills(peerId);
    }
}