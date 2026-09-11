using Charon.Features.PowerLevel;

namespace Charon.Tests.Features.PowerLevel;

public sealed class TagActionTableTests
{
    [Fact]
    public void MeleeTag_UnlocksAtFifteen()
    {
        Assert.Null(TagActionTable.Get(TagActionTable.Paladin, 14));
        var tag = TagActionTable.Get(TagActionTable.Paladin, 15);
        Assert.NotNull(tag);
        Assert.Equal(24u, tag!.ActionId); // Shield Lob
        Assert.False(tag.IsCast);
    }

    [Fact]
    public void ClassAndItsJob_ShareTheTag()
    {
        Assert.Same(TagActionTable.ForJob(TagActionTable.Gladiator), TagActionTable.ForJob(TagActionTable.Paladin));
        Assert.Same(TagActionTable.ForJob(TagActionTable.Conjurer), TagActionTable.ForJob(TagActionTable.WhiteMage));
        Assert.Same(TagActionTable.ForJob(TagActionTable.Rogue), TagActionTable.ForJob(TagActionTable.Ninja));
    }

    [Fact]
    public void Casters_TagFromLevelOne_AndAreCasts()
    {
        var stone = TagActionTable.Get(TagActionTable.Conjurer, 1);
        Assert.NotNull(stone);
        Assert.True(stone!.IsCast);
        Assert.Equal(25f, stone.Range);
    }

    [Fact]
    public void Scholar_UsesItsOwnRuinRow()
    {
        Assert.Equal(17869u, TagActionTable.Get(TagActionTable.Scholar, 30)!.ActionId);
        Assert.Equal(163u, TagActionTable.Get(TagActionTable.Arcanist, 1)!.ActionId);
    }

    [Fact]
    public void RedMage_JoltIsLevelTwo()
    {
        Assert.Null(TagActionTable.Get(TagActionTable.RedMage, 1));
        Assert.Equal(7503u, TagActionTable.Get(TagActionTable.RedMage, 2)!.ActionId);
    }

    [Fact]
    public void WeaponRangeJobs_ReachTwentyFive()
    {
        // The sheet says -1 ("weapon range") for these; ranged physical weapons reach 25y.
        Assert.Equal(25f, TagActionTable.Get(TagActionTable.Archer, 1)!.Range);
        Assert.Equal(25f, TagActionTable.Get(TagActionTable.Dancer, 1)!.Range);
    }

    [Fact]
    public void Beastmaster_TagsWithCapture()
    {
        // Capture is a 10y attack that also marks the beast: a pact if it dies while marked.
        var tag = TagActionTable.Get(TagActionTable.Beastmaster, 1);
        Assert.NotNull(tag);
        Assert.Equal(44880u, tag!.ActionId);
        Assert.Equal(10f, tag.Range);
        Assert.False(tag.IsCast);
    }

    [Theory]
    [InlineData(TagActionTable.Pugilist)]    // no ranged attack exists
    [InlineData(TagActionTable.Monk)]
    [InlineData(8u)]                         // a crafter
    public void JobsWithoutARangedTag_ReturnNull(uint classJobId)
    {
        Assert.Null(TagActionTable.ForJob(classJobId));
        Assert.Null(TagActionTable.Get(classJobId, 100));
    }
}
