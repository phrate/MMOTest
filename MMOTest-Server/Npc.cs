using System;
using System.Collections.Generic;
using Godot;

namespace MMOTest.Server;

public sealed class ActiveNpc
{
    // Immutable at runtime — set from DB at load.
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Display { get; init; } = "";
    public int TileX { get; init; }   // home tile (spawn point)
    public int TileY { get; init; }
    public int TileZ { get; init; }
    public string PlayingAnimation { get; init; } = "";
    public bool Attackable { get; init; } = true;
    public long? CombatLevelsId { get; init; }
    public int FinalLevel { get; init; } = 1;
    public string RenderSlug { get; init; } = "";
    public string AiPackageState { get; init; } = "idle";

    // Runtime mutable — set on load, updated by movement.
    public bool IsDead { get; set; }
    public int SpawnTimerSeconds { get; set; } = 3;
    public long DiedAtMs { get; set; } = 0;
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }
    public float Yaw { get; set; }
    public List<Vector2I> Path { get; set; } = new();

    // Broadcast tracking — diff against these to decide whether to send an
    // update this tick. NaN means "never sent", forcing the first send.
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

    /// <summary>Initialize runtime position from the home tile. Called once after load.</summary>
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