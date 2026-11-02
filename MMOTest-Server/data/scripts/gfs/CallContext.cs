using System;
using System.Collections.Generic;

namespace MMOTest.Server.Gfs;

public sealed class CallContext
{
    public Dictionary<string, object> System { get; set; } = new();
    public Dictionary<string, object> Locals { get; set; } = new();
    public Dictionary<string, object> Args { get; set; } = new();
    public long SelfPlayerId { get; set; }
    public string ScriptPath { get; set; } = "";
    public bool EndScriptRequested { get; set; }
    public CallNode? CurrentCall { get; set; }
    public List<Dictionary<string, object>> Effects { get; } = new();

    /// <summary>Invoked by RUN statements. Args are already evaluated.</summary>
    public Action<string, Dictionary<string, object>>? RunScript { get; set; }
}