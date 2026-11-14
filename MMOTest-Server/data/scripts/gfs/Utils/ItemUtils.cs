using System;

namespace MMOTest.Server.Gfs.Utils;

internal static class ItemUtils
{
    public static void Register(FunctionRegistry reg, ItemSystem inv,
                                GroundItemSystem ground, ServerState state)
    {
        reg.Register("GetSlotItem", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return null;
            int slot = Convert.ToInt32(args[0]);
            return inv.GetSlotItemSlug(ctx.SelfPlayerId, slot);
        });

        reg.Register("GetSlotQuantity", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return (long)0;
            int slot = Convert.ToInt32(args[0]);
            return (long)inv.GetSlotQuantity(ctx.SelfPlayerId, slot);
        });

        reg.Register("ConsumeSlot", (ctx, args) =>
        {
            if (args.Count < 2) return false;
            int slot = Convert.ToInt32(args[0]);
            int qty = Convert.ToInt32(args[1]);
            if (qty < 1) return false;
            return inv.ConsumeInventorySlot(ctx.SelfPlayerId, slot, qty);
        });

        reg.Register("ConsumeElixir", (ctx, args) =>
        {
            if (args.Count < 2) return false;
            int slot = Convert.ToInt32(args[0]);
            int heal = Convert.ToInt32(args[1]);
            return inv.TryConsumeItem(ctx.SelfPlayerId, slot, heal);
        });

        reg.Register("GiveItem", (ctx, args) =>
        {
            if (args.Count < 2) return false;
            string slug = args[0]?.ToString() ?? "";
            int qty = Convert.ToInt32(args[1]);
            if (string.IsNullOrEmpty(slug) || qty < 1) return false;
            return inv.GiveItem(ctx.SelfPlayerId, slug, qty);
        });

        reg.Register("HealPlayer", (ctx, args) =>
        {
            if (args.Count < 1) return null;
            int amt = Convert.ToInt32(args[0]);
            if (amt > 0) inv.HealPlayer(ctx.SelfPlayerId, amt);
            return null;
        });

        reg.Register("DropItem", (ctx, args) =>
        {
            if (args.Count < 2) return false;
            int slot = Convert.ToInt32(args[0]);
            int qty = Convert.ToInt32(args[1]);
            if (qty < 1) return false;
            return ground.TryDropFromSlot(ctx.SelfPlayerId, slot, qty);
        });

        reg.Register("PickupGroundItem", (ctx, args) =>
        {
            if (args.Count < 1) return null;
            long gid = Convert.ToInt64(args[0]);
            ground.RequestPickup(ctx.SelfPlayerId, gid);
            return null;
        });

        reg.Register("GiveCredits", (ctx, args) =>
        {
            if (args.Count < 1) return null;
            int amt = Convert.ToInt32(args[0]);
            if (amt > 0) inv.GiveCredits(ctx.SelfPlayerId, amt);
            return null;
        });

        reg.Register("GetCredits", (ctx, args) =>
        {
            if (!state.Credits.TryGetValue(ctx.SelfPlayerId, out int c)) return (long)0;
            return (long)c;
        });
    }
}