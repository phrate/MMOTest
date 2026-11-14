using System;

namespace MMOTest.Server.Gfs.Utils;

internal static class BankUtils
{
    public static void Register(FunctionRegistry reg, BankSystem bank)
    {
        reg.Register("RequestBank", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return null;
            long npcId;
            try { npcId = Convert.ToInt64(args[0]); } catch { return null; }
            if (npcId <= 0) return null;
            bank.RequestBank(ctx.SelfPlayerId, npcId);
            return null;
        });
    }
}