using System;
using System.Collections.Generic;

namespace MMOTest.Server.Gfs.Utils;

public static class ItemUtils
{
    public static void Register(FunctionRegistry reg, Main main)
    {
        // GetSlotItem(slot) -> item slug, or null if empty
        reg.Register("GetSlotItem", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return null;
            int slot = Convert.ToInt32(args[0]);
            return main.GetSlotItemSlug(ctx.SelfPlayerId, slot);
        });

        // GetSlotQuantity(slot) -> int, 0 if empty
        reg.Register("GetSlotQuantity", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return (long)0;
            int slot = Convert.ToInt32(args[0]);
            return (long)main.GetSlotQuantity(ctx.SelfPlayerId, slot);
        });

        // ConsumeSlot(slot, quantity) -> true if consumed
        reg.Register("ConsumeSlot", (ctx, args) =>
        {
            if (args.Count < 2) return false;
            int slot = Convert.ToInt32(args[0]);
            int qty = Convert.ToInt32(args[1]);
            if (qty < 1) return false;
            return main.ConsumeInventorySlot(ctx.SelfPlayerId, slot, qty);
        });

        // GiveItem(slug, quantity) -> true if added
        reg.Register("GiveItem", (ctx, args) =>
        {
            if (args.Count < 2) return false;
            string slug = args[0]?.ToString() ?? "";
            int qty = Convert.ToInt32(args[1]);
            if (string.IsNullOrEmpty(slug) || qty < 1) return false;
            return main.GiveItem(ctx.SelfPlayerId, slug, qty);
        });

        // HealPlayer(amount)
        reg.Register("HealPlayer", (ctx, args) =>
        {
            if (args.Count < 1) return null;
            int amt = Convert.ToInt32(args[0]);
            if (amt > 0) main.HealPlayer(ctx.SelfPlayerId, amt);
            return null;
        });
    }
}