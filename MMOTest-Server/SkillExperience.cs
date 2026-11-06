using System;
using System.Collections.Generic;

namespace MMOTest.Server;

public static class SkillExperience
{
    public const int MaxLevel = 100;
    public const int XpPrecision = 10;
    public const int AccuracyXpTenthsPerDamage = 80;
    public const int HitpointsXpTenthsPerDamage = 26;

    private static readonly int[] XpThresholds =
    {
        0, 41, 87, 138, 194, 256, 325, 400, 484, 577,
        679, 792, 916, 1053, 1205, 1373, 1557, 1761, 1986, 2235,
        2509, 2812, 3145, 3514, 3921, 4370, 4865, 5412, 6015, 6681,
        7416, 8228, 9123, 10112, 11203, 12407, 13736, 15204, 16824, 18612,
        20585, 22764, 25169, 27824, 30756, 33991, 37563, 41507, 45860, 50666,
        55972, 61830, 68297, 75436, 83318, 92020, 101627, 112233, 123943, 136871,
        151144, 166902, 184299, 203507, 224714, 248127, 273976, 302516, 334025, 368813,
        407222, 449628, 496447, 548139, 605210, 668221, 737790, 814600, 899404, 993034,
        1096409, 1210543, 1336557, 1475686, 1629297, 1798896, 1986147, 2192888, 2421147, 2673166,
        2951415, 3258626, 3597814, 3972307, 4385779, 4842288, 5346314, 5902803, 6517215, 7195580,
    };

    public static IReadOnlyList<int> Thresholds { get; } = Array.AsReadOnly(XpThresholds);

    public static int GetXpForLevel(int level)
    {
        if (level < 1 || level > MaxLevel)
            throw new ArgumentOutOfRangeException(nameof(level));

        return XpThresholds[level - 1];
    }

    public static long GetXpForLevelTenths(int level)
        => (long)GetXpForLevel(level) * XpPrecision;

    public static int GetDisplayedXp(long xpTenths)
    {
        if (xpTenths < 0)
            throw new ArgumentOutOfRangeException(nameof(xpTenths));

        return checked((int)(xpTenths / XpPrecision));
    }

    public static int GetDisplayedXpToNextLevel(long xpTenths, int currentLevel)
    {
        if (currentLevel < 1 || currentLevel > MaxLevel)
            throw new ArgumentOutOfRangeException(nameof(currentLevel));
        if (currentLevel == MaxLevel)
            return 0;

        long remainingTenths = GetXpForLevelTenths(currentLevel + 1) - xpTenths;
        if (remainingTenths <= 0)
            return 0;

        return checked((int)((remainingTenths + XpPrecision - 1) / XpPrecision));
    }

    public static int GetLevelForXp(int xp)
    {
        if (xp < 0)
            throw new ArgumentOutOfRangeException(nameof(xp));

        return GetLevelForXpTenths((long)xp * XpPrecision);
    }

    public static int GetLevelForXpTenths(long xpTenths)
    {
        if (xpTenths < 0)
            throw new ArgumentOutOfRangeException(nameof(xpTenths));

        int lowerBound = 0;
        int upperBound = XpThresholds.Length;
        while (lowerBound < upperBound)
        {
            int middle = lowerBound + (upperBound - lowerBound) / 2;
            if ((long)XpThresholds[middle] * XpPrecision <= xpTenths)
                lowerBound = middle + 1;
            else
                upperBound = middle;
        }

        return Math.Max(1, lowerBound);
    }
}