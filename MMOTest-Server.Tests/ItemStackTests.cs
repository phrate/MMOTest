using Xunit;
using MMOTest.Server;

namespace MMOTest.Server.Tests;

public class ItemStackTests
{
    [Fact]
    public void Encode_RoundTrips_TypicalValues()
    {
        long packed = ItemStack.Encode(1234, 500);
        Assert.Equal(1234, ItemStack.ItemId(packed));
        Assert.Equal(500, ItemStack.Quantity(packed));
        Assert.False(ItemStack.IsEmpty(packed));
    }

    [Fact]
    public void Encode_Empty_IsZero()
    {
        // The "empty slot" sentinel used everywhere in the inventory path.
        Assert.Equal(0L, ItemStack.Encode(0, 0));
        Assert.True(ItemStack.IsEmpty(ItemStack.Encode(0, 0)));
    }

    [Fact]
    public void Encode_QuantityOne_NonZeroId_NotEmpty()
    {
        // Sanity: a single item is not "empty" even though its lower bits are 1.
        long packed = ItemStack.Encode(42, 1);
        Assert.False(ItemStack.IsEmpty(packed));
        Assert.Equal(42, ItemStack.ItemId(packed));
        Assert.Equal(1, ItemStack.Quantity(packed));
    }

    [Fact]
    public void Encode_MaxItemId_RoundTrips()
    {
        long packed = ItemStack.Encode(int.MaxValue, 1);
        Assert.Equal(int.MaxValue, ItemStack.ItemId(packed));
        Assert.Equal(1, ItemStack.Quantity(packed));
    }

    [Fact]
    public void Encode_MaxQuantity_RoundTrips()
    {
        // Quantities are stored as uint in the low 32 bits; int.MaxValue must
        // survive the round-trip. Values above int.MaxValue can't be produced
        // by callers, but the encoding is symmetric for any uint.
        long packed = ItemStack.Encode(1, int.MaxValue);
        Assert.Equal(1, ItemStack.ItemId(packed));
        Assert.Equal(int.MaxValue, ItemStack.Quantity(packed));
    }

    [Fact]
    public void Encode_QuantityZero_NonZeroId_NotEmptyButEmptyQuantity()
    {
        // Not a case the game produces (callers guard qty > 0), but the
        // encoding must remain consistent if it ever slips through.
        long packed = ItemStack.Encode(42, 0);
        Assert.Equal(42, ItemStack.ItemId(packed));
        Assert.Equal(0, ItemStack.Quantity(packed));
        Assert.False(ItemStack.IsEmpty(packed));   // id != 0
    }

    [Fact]
    public void ItemIdAndQuantity_AreIndependent()
    {
        // Verify the two halves don't bleed into each other. Both fields
        // set to their int.MaxValue ceiling in a single packed long.
        long a = ItemStack.Encode(int.MaxValue, int.MaxValue);
        Assert.Equal(int.MaxValue, ItemStack.ItemId(a));
        Assert.Equal(int.MaxValue, ItemStack.Quantity(a));

        long b = ItemStack.Encode(1, int.MaxValue);
        Assert.Equal(1, ItemStack.ItemId(b));
        Assert.Equal(int.MaxValue, ItemStack.Quantity(b));
    }
}