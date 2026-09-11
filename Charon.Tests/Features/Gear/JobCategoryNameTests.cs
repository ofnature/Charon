using Charon.Features.Gear;

namespace Charon.Tests.Features.Gear;

public sealed class JobCategoryNameTests
{
    [Theory]
    [InlineData("PGL MNK SAM BST", true)]
    [InlineData("GLA THM PLD BLM BST", true)]
    [InlineData("All Classes", false)]           // a description, not a list
    [InlineData("Disciple of War", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsJobList_OnlyAcceptsAllThreeLetterCodes(string name, bool expected)
    {
        Assert.Equal(expected, JobCategoryName.IsJobList(name));
    }

    [Theory]
    [InlineData("PGL MNK SAM BST", "BST", true)]
    [InlineData("PGL LNC MNK DRG SAM RPR BST", "BST", true)]
    [InlineData("PGL MNK SAM BST", "PLD", false)]
    [InlineData("BSM CRP ARM", "BST", false)]    // BST is not BSM — whole tokens only
    [InlineData("All Classes", "BST", false)]
    public void Mentions_MatchesWholeTokensOnly(string name, string abbreviation, bool expected)
    {
        Assert.Equal(expected, JobCategoryName.Mentions(name, abbreviation));
    }

    [Fact]
    public void Mentions_IsCaseSensitive_BecauseTheSheetCodesAreUppercase()
    {
        Assert.False(JobCategoryName.Mentions("PGL MNK SAM BST", "bst"));
    }
}
