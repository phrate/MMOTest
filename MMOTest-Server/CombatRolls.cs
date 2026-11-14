using System;

namespace MMOTest.Server;

/// <summary>
/// Pure combat math: hit chance and damage rolls. Extracted from Main so it
/// can be unit-tested without spinning up Godot, sockets, or any player state.
/// The Random is passed in by the caller (Main uses _combatRng) so tests can
/// seed it for determinism.
/// </summary>
public static class CombatRolls
{
    public static bool RollHit(int accuracy, int accuracyBonus, int defence, int defenceBonus, Random rng)
    {
        int effectiveAcc = Math.Max(1, accuracy) + 8;
        int attackRoll = effectiveAcc * (accuracyBonus + 64);
        int effectiveDef = Math.Max(1, defence) + 8;
        int defenceRoll = effectiveDef * (defenceBonus + 64);
        if (attackRoll > defenceRoll)
            return rng.NextDouble() < 1.0 - (defenceRoll + 2.0) / (2.0 * (attackRoll + 1.0));
        return rng.NextDouble() < (double)attackRoll / (2.0 * (defenceRoll + 1.0));
    }

    public static int RollDamage(int strength, int strengthBonus, Random rng)
    {
        int effectiveStr = Math.Max(1, strength) + 8;
        int maxHit = (int)MathF.Floor(0.5f + effectiveStr * (strengthBonus + 64) / 640f);
        if (maxHit < 1) maxHit = 1;
        return rng.Next(0, maxHit + 1);
    }
}