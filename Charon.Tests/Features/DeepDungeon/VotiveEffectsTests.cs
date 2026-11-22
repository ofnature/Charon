using System.Linq;
using Charon.Features.DeepDungeon;
using Xunit;

namespace Charon.Tests.Features.DeepDungeon;

public class VotiveEffectsTests
{
    [Fact]
    public void EveryRowOfTheGamesSheet_HasAnEffect()
    {
        // DeepDungeon4GimmickEffectTransient rows 1-10.
        for (var id = 1; id <= 10; id++)
        {
            var effect = VotiveEffects.For(id);
            Assert.NotNull(effect);
            Assert.Equal(id, effect!.Id);
            Assert.NotEmpty(effect.Short);
            Assert.NotEmpty(effect.Detail);
        }
    }

    [Theory]
    [InlineData(0)] // none lit
    [InlineData(11)]
    [InlineData(-1)]
    public void NoVotive_AndAnUnknownRow_HaveNoEffectText(int id)
    {
        Assert.Null(VotiveEffects.For(id));
    }

    [Fact]
    public void OnlyTheLastThreeAreHarmful()
    {
        Assert.Equal(new[] { 8, 9, 10 }, VotiveEffects.All.Where(e => e.Harmful).Select(e => e.Id).ToArray());
    }

    [Fact]
    public void NamesAreTheSheets()
    {
        Assert.Equal("Immolation", VotiveEffects.For(2)!.Name);
        Assert.Equal("Foes' Frailty", VotiveEffects.For(7)!.Name);
        Assert.Equal("Blind Faith", VotiveEffects.For(10)!.Name);
    }

    [Fact]
    public void TheShortLine_FitsTheSideColumn()
    {
        // The side column is as wide as its longest line; these sit under a votive's name.
        Assert.All(VotiveEffects.All, e => Assert.True(e.Short.Length <= 30, e.Short));
    }
}
