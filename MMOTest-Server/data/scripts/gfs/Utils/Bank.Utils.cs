using System;
using System.Collections.Generic;

namespace MMOTest.Server.Gfs.Utils;

public static class BankUtils
{
    public static void Register(FunctionRegistry reg, Main main)
    {
        reg.Register("RequestBank", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return null;
            long npcId;
            try { npcId = Convert.ToInt64(args[0]); } catch { return null; }
            if (npcId <= 0) return null;
            main.RequestBank(ctx.SelfPlayerId, npcId);
            return null;
        });
    }
}