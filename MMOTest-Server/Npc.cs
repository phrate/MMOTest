namespace MMOTest.Server;

public sealed class ActiveNpc
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Display { get; init; } = "";
    public int TileX { get; init; }
    public int TileY { get; init; }
    public int TileZ { get; init; }
    public string PlayingAnimation { get; init; } = "";
    public bool IsDead { get; init; }
    public bool Attackable { get; init; } = true;
    public long? CombatLevelsId { get; init; }
    public int FinalLevel { get; init; } = 1;
    public string RenderSlug { get; init; } = "";

    public int ChunkX(int chunkSize) => FloorDiv(TileX, chunkSize);
    public int ChunkY(int chunkSize) => FloorDiv(TileY, chunkSize);

    public static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }
}