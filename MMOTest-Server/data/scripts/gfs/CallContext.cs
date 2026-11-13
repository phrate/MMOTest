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
    public string? ReturnCode { get; set; }                          // NEW
    public List<Dictionary<string, object>> Effects { get; } = new();

    public Action<string, Dictionary<string, object>>? RunScript { get; set; }
    public Func<string, SuiteNode?>? TaskLookup { get; set; }

    // Recipe-context state.
    public bool IsRecipeContext { get; set; }
    public bool RecipeFailed { get; set; }
    public List<(int ItemId, int Quantity)> RecipeConsume { get; } = new();
    public List<(int ItemId, int Quantity)> RecipeOutput { get; } = new();
    public Dictionary<string, List<AstNode>> RecipeFailMessages { get; } = new();
    public Dictionary<string, List<AstNode>> RecipeSuccessMessages { get; } = new();

    // Resolvers, wired by the caller at execution time. For [recipe] scripts:
    //   ResolveIdent: "item_X" -> item id, "level_X" -> player skill level, else null
    //   CountItemInInventory: item id -> total quantity across the player's inventory
    public Func<string, object?>? ResolveIdent { get; set; }
    public Func<int, int>? CountItemInInventory { get; set; }
}