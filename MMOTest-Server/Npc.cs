using System;
using System.Collections.Generic;
using Godot;

namespace MMOTest.Server;

public sealed class ActiveNpc
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public int TileX { get; init; }
    public int TileY { get; init; }
    public int TileZ { get; init; }
    public string PlayingAnimation { get; init; } = "";
    public long? CombatLevelsId { get; init; }
    public int FinalLevel { get; init; } = 1;
    public string RenderSlug { get; init; } = "";

    // Editable at runtime via admin NPC editor.
    public string Display { get; set; } = "";
    public bool Attackable { get; set; } = true;
    public string AiPackageState { get; set; } = "idle";
    public string AiActionState { get; set; } = "none";
    public string LootTableName { get; set; } = "common_loot_1";
    public int SpawnTimerSeconds { get; set; } = 3;
    public int Hitpoints { get; set; } = 3;
    public int MaxHitpoints { get; set; } = 3;
    public int Accuracy { get; set; } = 1;
    public int Strength { get; set; } = 1;
    public int Defence { get; set; } = 1;
    public int MeleeBonus { get; set; } = 0;
    public int MeleeDefense { get; set; } = 0;
    public int AttackSpeedTicks { get; set; } = 10;   // 3.0s
    public bool Aggressive { get; set; } = false;
    public int AggroRangeTiles { get; set; } = 8;

    // Runtime mutable.
    public bool IsDead { get; set; }
    public long DiedAtMs { get; set; }
    public long HealAccumulatorTicks { get; set; } = 0;
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }
    public float Yaw { get; set; }
    public List<Vector2I> Path { get; set; } = new();
    public bool ReturningHome { get; set; } = false;

    public float LastSentX { get; set; } = float.NaN;
    public float LastSentY { get; set; } = float.NaN;
    public float LastSentZ { get; set; } = float.NaN;
    public float LastSentYaw { get; set; } = float.NaN;

    public bool IsMoving => Path.Count > 0;

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
}