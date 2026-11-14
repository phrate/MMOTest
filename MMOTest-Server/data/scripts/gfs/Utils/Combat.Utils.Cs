using System;

namespace MMOTest.Server.Gfs.Utils;

internal static class CombatUtils
{
    public static void Register(FunctionRegistry reg, CombatSystem combat, TradeSystem trade, NpcSystem npc)
    {
        reg.Register("KillNpc", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return null;
            long npcId;
            try { npcId = Convert.ToInt64(args[0]); } catch { return null; }
            if (npcId <= 0) return null;
            combat.KillNpc(npcId, ctx.SelfPlayerId);
            return null;
        });

        reg.Register("EngageNpc", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return null;
            long npcId;
            try { npcId = Convert.ToInt64(args[0]); } catch { return null; }
            if (npcId <= 0) return null;
            combat.RequestEngage(ctx.SelfPlayerId, npcId);
            return null;
        });

        reg.Register("RequestTrade", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return null;
            long targetId;
            try { targetId = Convert.ToInt64(args[0]); } catch { return null; }
            if (targetId <= 0) return null;
            trade.RequestTrade(ctx.SelfPlayerId, targetId);
            return null;
        });

        reg.Register("OpenNpcEditor", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return null;
            long npcId;
            try { npcId = Convert.ToInt64(args[0]); } catch { return null; }
            if (npcId <= 0) return null;
            npc.OpenNpcEditorForPlayer(ctx.SelfPlayerId, npcId);
            return null;
        });
    }
}