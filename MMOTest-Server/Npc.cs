using System;
using System.Collections.Generic;
using Godot;

namespace MMOTest.Server;

/// <summary>
/// A single placed NPC in the world. Instance state (tile, HP, effects,
/// movement, AI tick counters) lives here. Static definition (display,
/// render, combat maxes, loot, dialogue) delegates to the shared Type record.
/// Editing the type updates every instance of it on next respawn.
///
/// Persistence model:
///   - Persisted instance state: tile coords, playing_animation, is_dead,
///     died_at, name_override
///   - Transient instance state: hitpoints, current stats, debuff flags,
///     position lerp, path, AI timers. These reset on server restart — the
///     intent is that debuffs expire, HP regenerates, and short-lived
///     effects don't need durability.
/// </summary>
public sealed class ActiveNpc
{
    // -------- Identity --------
    public long Id { get; init; }
    public NpcTypeRecord Type { get; init; } = null!;

    // -------- Persisted instance state --------
    public string NameOverride { get; set; } = "";
    public int TileX { get; set; }
    public int TileY { get; set; }
    public int TileZ { get; set; }
    public string PlayingAnimation { get; set; } = "";
    public bool IsDead { get; set; }
    public bool IsDying { get; set; }
    public long DiedAtMs { get; set; }

    // -------- Transient instance state --------
    public int Hitpoints { get; set; }
    public int CurrentAccuracy { get; set; }
    public int CurrentStrength { get; set; }
    public int CurrentDefence { get; set; }
    public int CurrentEther { get; set; }
    public bool Poisoned { get; set; }
    public bool Burning { get; set; }
    public bool Frozen { get; set; }

    // -------- Runtime movement / AI state (never persisted) --------
    public long HealAccumulatorTicks { get; set; }
    public string AiActionState { get; set; } = "none";
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }
    public float Yaw { get; set; }
    public List<Vector2I> Path { get; set; } = new();
    public bool ReturningHome { get; set; }

    public float LastSentX { get; set; } = float.NaN;
    public float LastSentY { get; set; } = float.NaN;
    public float LastSentZ { get; set; } = float.NaN;
    public float LastSentYaw { get; set; } = float.NaN;

    // -------- Convenience --------
    public bool IsMoving => Path.Count > 0;

    // Identity / display
    public string Display => string.IsNullOrEmpty(NameOverride) ? Type.Display : NameOverride;
    public string Name => Display;
    public string SlugId => Type.SlugId;
    public string RenderSlug => Type.RenderSlug;

    // Combat — current values (possibly debuffed). CombatSystem reads these.
    public int Accuracy => CurrentAccuracy;
    public int Strength => CurrentStrength;
    public int Defence => CurrentDefence;
    public int Ether => CurrentEther;

    // Combat — max values from the type
    public int MaxHitpoints => Type.MaxHitpoints;
    public int MaxAccuracy => Type.MaxAccuracy;
    public int MaxStrength => Type.MaxStrength;
    public int MaxDefence => Type.MaxDefence;
    public int MaxEther => Type.MaxEther;
    public int FinalLevel => Type.FinalLevel;
    public int MeleeBonus => Type.MeleeBonus;
    public int RangedBonus => Type.RangedBonus;
    public int EtherBonus => Type.EtherBonus;
    public int MeleeDefense => Type.MeleeDefense;
    public int RangedDefense => Type.RangedDefense;
    public int EtherDefense => Type.EtherDefense;
    public int AttackSpeedTicks => Type.AttackSpeedTicks;

    // Behavior / interaction flags
    public bool Attackable => Type.Attackable;
    public bool Aggressive => Type.Aggressive;
    public bool IsBanker => Type.IsBanker;
    public int AggroRangeTiles => Type.AggroRangeTiles;
    public string AiPackageState => Type.AiPackageState;
    public string SpawnFlags => Type.SpawnFlags;
    public int SpawnTimerSeconds => Type.SpawnTimerSeconds;
    public string LootTableName => Type.LootTableName;
    public string CreditsTableName => Type.CreditsTableName;
    public string DialogueScript => Type.DialogueScript;
    
    public int ChunkX(int chunkSize) => FloorDiv((int)MathF.Round(PosX), chunkSize);
    public int ChunkY(int chunkSize) => FloorDiv((int)MathF.Round(PosZ), chunkSize);

    public static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }

    public void ResetPositionToHome()
    {
        PosX = TileX;
        PosY = TileZ;
        PosZ = TileY;
        Yaw = 0f;
        LastSentX = PosX;
        LastSentY = PosY;
        LastSentZ = PosZ;
        LastSentYaw = Yaw;
    }

    /// <summary>
    /// Re-apply the type's max values as the current values. Called on spawn
    /// and on respawn. Clears all debuffs.
    /// </summary>
    public void ResetToTypeDefaults()
    {
        Hitpoints = Type.MaxHitpoints;
        CurrentAccuracy = Type.MaxAccuracy;
        CurrentStrength = Type.MaxStrength;
        CurrentDefence = Type.MaxDefence;
        CurrentEther = Type.MaxEther;
        Poisoned = false;
        Burning = false;
        Frozen = false;
        HealAccumulatorTicks = 0;
    }
}