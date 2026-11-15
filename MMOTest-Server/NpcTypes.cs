namespace MMOTest.Server;

/// <summary>
/// A definition shared across many placements. Editing a type affects every
/// instance live at next spawn/refresh. Combat stats, appearance, loot, AI
/// package, dialogue — all live here.
/// </summary>
public sealed class NpcTypeRecord
{
    public long Id { get; init; }
    public string SlugId { get; set; } = "";
    public string Display { get; set; } = "";
    public string RenderSlug { get; set; } = "";
    public string DialogueScript { get; set; } = "";
    public bool IsBanker { get; set; }
    public bool Attackable { get; set; } = true;
    public bool Aggressive { get; set; }
    public int AggroRangeTiles { get; set; } = 8;
    public string AiPackageState { get; set; } = "idle";
    public int SpawnTimerSeconds { get; set; } = 3;
    public string SpawnFlags { get; set; } = "{}";
    public string LootTableName { get; set; } = "common_loot_1";
    public string CreditsTableName { get; set; } = "";

    // Combat (authoritative max values)
    public int FinalLevel { get; set; } = 1;
    public int MaxHitpoints { get; set; } = 10;
    public int MaxAccuracy { get; set; } = 1;
    public int MaxStrength { get; set; } = 1;
    public int MaxDefence { get; set; } = 1;
    public int MaxEther { get; set; } = 1;
    public int MeleeBonus { get; set; }
    public int RangedBonus { get; set; }
    public int EtherBonus { get; set; }
    public int MeleeDefense { get; set; }
    public int RangedDefense { get; set; }
    public int EtherDefense { get; set; }
    public int AttackSpeedTicks { get; set; } = 10;
}