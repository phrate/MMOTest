using System;
using System.Collections.Generic;

namespace MMOTest.Server.Gfs.Utils;

public static class CombatUtils
{
    public static void Register(FunctionRegistry reg, Main main)
    {
        reg.Register("KillNpc", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return null;
            long npcId;
            try { npcId = Convert.ToInt64(args[0]); } catch { return null; }
            if (npcId <= 0) return null;
            main.KillNpc(npcId, ctx.SelfPlayerId);
            return null;
        });

        reg.Register("EngageNpc", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return null;
            long npcId;
            try { npcId = Convert.ToInt64(args[0]); } catch { return null; }
            if (npcId <= 0) return null;
            main.RequestEngage(ctx.SelfPlayerId, npcId);
            return null;
        });

        reg.Register("OpenNpcEditor", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return null;
            long npcId;
            try { npcId = Convert.ToInt64(args[0]); } catch { return null; }
            if (npcId <= 0) return null;
            main.OpenNpcEditorForPlayer(ctx.SelfPlayerId, npcId);
            return null;
        });
    }
}