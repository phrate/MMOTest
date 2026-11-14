using System;
using Xunit;
using MMOTest.Server;

namespace MMOTest.Server.Tests;

public class CombatRollsTests
{
    // --- RollDamage ---

    [Fact]
    public void RollDamage_Minimum_Strength_Is_Zero_Or_One()
    {
        // strength=1: effectiveStr=9, maxHit=floor(0.5 + 9*64/640)=floor(1.4)=1
        var rng = new Random(42);
        for (int i = 0; i < 100; i++)
        {
            int dmg = CombatRolls.RollDamage(1, 0, rng);
            Assert.InRange(dmg, 0, 1);
        }
    }

    [Fact]
    public void RollDamage_Strength99_Never_Exceeds_Eleven()
    {
        // strength=99: effectiveStr=107, maxHit=floor(0.5 + 107*64/640)=floor(11.2)=11
        var rng = new Random(42);
        for (int i = 0; i < 200; i++)
        {
            int dmg = CombatRolls.RollDamage(99, 0, rng);
            Assert.InRange(dmg, 0, 11);
        }
    }

    [Fact]
    public void RollDamage_StrengthBonus_Increases_MaxHit()
    {
        // strength=99, bonus=64: maxHit=floor(0.5 + 107*128/640)=floor(21.9)=21
        var rng = new Random(42);
        for (int i = 0; i < 200; i++)
        {
            int dmg = CombatRolls.RollDamage(99, 64, rng);
            Assert.InRange(dmg, 0, 21);
        }
    }

    [Fact]
    public void RollDamage_Never_Negative()
    {
        var rng = new Random(42);
        for (int i = 0; i < 100; i++)
        {
            Assert.True(CombatRolls.RollDamage(1, 0, rng) >= 0);
        }
    }

    // --- RollHit ---

    [Fact]
    public void RollHit_Massive_Attack_Almost_Always_Hits()
    {
        // accuracy=1000, defence=1: chance ≈ 0.9955
        var rng = new Random(42);
        int hits = 0;
        for (int i = 0; i < 1000; i++)
            if (CombatRolls.RollHit(1000, 0, 1, 0, rng)) hits++;
        Assert.InRange(hits, 950, 1000);
    }

    [Fact]
    public void RollHit_Tiny_Attack_Rarely_Hits()
    {
        // accuracy=1, defence=1000: chance ≈ 0.0045
        var rng = new Random(42);
        int hits = 0;
        for (int i = 0; i < 1000; i++)
            if (CombatRolls.RollHit(1, 0, 1000, 0, rng)) hits++;
        Assert.InRange(hits, 0, 20);
    }

    [Fact]
    public void RollHit_Equal_Stats_Hits_Roughly_Half()
    {
        // accuracy=defence=50, no bonus: chance = 0.5 exactly by symmetry
        var rng = new Random(42);
        int hits = 0;
        for (int i = 0; i < 1000; i++)
            if (CombatRolls.RollHit(50, 0, 50, 0, rng)) hits++;
        Assert.InRange(hits, 400, 600);
    }

    [Fact]
    public void RollHit_Zero_Accuracy_Still_Clamps_To_Minimum()
    {
        // Accuracy clamped to 1 by Math.Max. Should still produce hits sometimes,
        // not throw or divide by zero.
        var rng = new Random(42);
        int hits = 0;
        for (int i = 0; i < 100; i++)
            if (CombatRolls.RollHit(0, 0, 1, 0, rng)) hits++;
        Assert.True(hits >= 0);  // just verifying no exception
    }

    // --- Determinism ---

    [Fact]
    public void Seeded_Rng_Gives_Reproducible_Results()
    {
        var rng1 = new Random(12345);
        var rng2 = new Random(12345);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(
                CombatRolls.RollDamage(50, 5, rng1),
                CombatRolls.RollDamage(50, 5, rng2));
        }
    }

    [Fact]
    public void Seeded_Rng_Produces_Same_Hit_Sequence()
    {
        var rng1 = new Random(999);
        var rng2 = new Random(999);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(
                CombatRolls.RollHit(50, 10, 40, 5, rng1),
                CombatRolls.RollHit(50, 10, 40, 5, rng2));
        }
    }
}