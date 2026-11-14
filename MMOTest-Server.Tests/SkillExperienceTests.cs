using Xunit;
using MMOTest.Server;

namespace MMOTest.Server.Tests;

public class SkillExperienceTests
{
    [Fact]
    public void Level1_Requires_ZeroXp()
    {
        Assert.Equal(0, SkillExperience.GetXpForLevel(1));
    }

    [Fact]
    public void Level2_Requires_41Xp()
    {
        Assert.Equal(41, SkillExperience.GetXpForLevel(2));
    }

    [Fact]
    public void Level2_Requires_410_Tenths()
    {
        Assert.Equal(410, SkillExperience.GetXpForLevelTenths(2));
    }

    [Fact]
    public void ZeroXp_Is_Level1()
    {
        Assert.Equal(1, SkillExperience.GetLevelForXpTenths(0));
    }

    [Fact]
    public void JustBelowLevel2_Is_Level1()
    {
        Assert.Equal(1, SkillExperience.GetLevelForXpTenths(409));
    }

    [Fact]
    public void ExactlyLevel2_Is_Level2()
    {
        Assert.Equal(2, SkillExperience.GetLevelForXpTenths(410));
    }

    [Fact]
    public void MaxLevel_Is100()
    {
        Assert.Equal(100, SkillExperience.MaxLevel);
    }

    [Fact]
    public void GetXpForLevel_Zero_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => SkillExperience.GetXpForLevel(0));
    }
}