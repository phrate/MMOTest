using System;

namespace MMOTest.Server.Gfs.Utils;

internal static class DialogueUtils
{
    public static void Register(FunctionRegistry reg, DialogueSystem dialogue)
    {
        reg.Register("RequestTalk", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return null;
            long npcId;
            try { npcId = Convert.ToInt64(args[0]); } catch { return null; }
            if (npcId <= 0) return null;
            dialogue.RequestTalk(ctx.SelfPlayerId, npcId);
            return null;
        });
    }
}