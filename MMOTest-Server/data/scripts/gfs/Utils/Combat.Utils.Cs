using System;
using System.Collections.Generic;

namespace MMOTest.Server.Gfs.Utils;

public static class CombatUtils
{
    public static void Register(FunctionRegistry reg, Main main)
    {
        // KillNpc(npc_id) -> marks the NPC dead, starts the respawn timer.
        // Persists to DB, broadcasts npc_left to nearby players, and (via the
        // RunTick respawn sweep) brings it back after spawn_timer seconds.
        reg.Register("KillNpc", (ctx, args) =>
        {
            if (args.Count < 1 || args[0] == null) return null;
            long npcId;
            try { npcId = Convert.ToInt64(args[0]); } catch { return null; }
            if (npcId <= 0) return null;
            main.KillNpc(npcId);
            return null;
        });
    }
}