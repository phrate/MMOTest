namespace MMOTest.Server;

public static class ItemStack
{
    // 32 bits for item_id, 32 bits for quantity. 0 = empty slot.
    public static long Encode(int itemId, int quantity)
        => ((long)itemId << 32) | (uint)quantity;

    public static int ItemId(long packed) => (int)(packed >> 32);
    public static int Quantity(long packed) => (int)(uint)(packed & 0xFFFFFFFF);
    public static bool IsEmpty(long packed) => packed == 0;
}