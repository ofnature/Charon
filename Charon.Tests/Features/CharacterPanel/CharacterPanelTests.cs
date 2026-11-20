using Charon.Features.CharacterPanel;
using Charon.Services.Game.CharacterPanel;

namespace Charon.Tests.Features.CharacterPanel;

/// <summary>What Charon added to or changed in the Character Panel Refined port.</summary>
public class CharacterPanelTests
{
    [Fact]
    public void BeastmasterIsAStrengthMeleeJobWithTheSheetsNumbers()
    {
        Assert.Equal(43, (int)JobId.BST);
        Assert.Equal(110, JobId.BST.AttackModifier());
        Assert.Equal(110, JobId.BST.HpModifier());
        Assert.False(JobId.BST.IsCaster());
        Assert.False(JobId.BST.UsesDexterity());
        Assert.False(JobId.BST.UsesTenacity());
        Assert.Equal(1, JobId.BST.TraitModifiers(100));
    }

    [Fact]
    public void AJobTheTableDoesNotKnow_ScoresNoDamageRatherThanThrowing()
    {
        var modifier = LevelModifiers.LevelTable[100];

        var result = Equations.CalcExpectedOutput(100, 5000, 150, (JobId)99, 0, 1.4, 0.05, 0, 0, modifier);

        Assert.True(result.AvgDamage > 0);
    }

    [Theory]
    [InlineData(1000, 50, false, 0, 950)] // a flat bonus comes straight off
    [InlineData(1100, 10, true, 200, 1000)] // 10% of the base 1000
    [InlineData(1100, 10, true, 60, 1040)] // 10%, but the food caps at 60
    public void FoodBonusIsTakenBackOff(int current, int value, bool relative, int max, int expected)
        => Assert.Equal(expected, Equations.WithoutFoodBonus(current, value, relative, max));

    [Fact]
    public void EveryLevelFromOneToOneHundredHasModifiers()
    {
        for (var level = 1; level <= 100; level++)
            Assert.True(LevelModifiers.LevelTable.ContainsKey(level), $"level {level}");

        Assert.False(LevelModifiers.LevelTable.ContainsKey(0));
    }

    /// <summary>
    /// The original's own check: every tooltip template has the placeholders its update writes into.
    /// A missing one would throw inside the Character window's update.
    /// </summary>
    [Fact]
    public void EveryTooltipTemplateTakesItsValues()
    {
        using var tooltips = new Tooltips();
        tooltips.Reload();

        var stat = new StatInfo { CurrentValue = 1000, DisplayValue = 0.5, NextTier = 1010, PrevTier = 999 };
        var mod = GcdModifier.LeyLines;
        var passive = GcdModifier.Huton;

        foreach (var entry in Enum.GetValues<Tooltips.Entry>())
        {
            switch (entry)
            {
                case Tooltips.Entry.MainStat:
                case Tooltips.Entry.ItemLevelSync:
                    break;
                case Tooltips.Entry.ExpectedDamage:
                case Tooltips.Entry.ExpectedHeal:
                    tooltips.UpdateExpectedOutput(entry, 1, 1.5);
                    break;
                case Tooltips.Entry.Speed:
                    tooltips.UpdateSpeed(stat, stat, stat, 250, null, null);
                    tooltips.UpdateSpeed(stat, stat, stat, 250, 280, null);
                    tooltips.UpdateSpeed(stat, stat, stat, 250, null, mod);
                    tooltips.UpdateSpeed(stat, stat, stat, 250, 280, mod);
                    tooltips.UpdateSpeed(stat, stat, stat, 250, null, passive);
                    tooltips.UpdateSpeed(stat, stat, stat, 250, 280, passive);
                    break;
                case Tooltips.Entry.Vitality:
                    tooltips.UpdateVitality("BST", 30.1, 1.1);
                    break;
                case Tooltips.Entry.Tenacity:
                    tooltips.UpdateTenacity(entry, stat, stat);
                    break;
                default:
                    tooltips.Update(entry, stat);
                    break;
            }
        }
    }
}
