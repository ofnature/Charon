using Charon;
using Charon.Features.Gear;

namespace Charon.Tests.Features.Gear;

public sealed class ExpBonusItemsTests
{
    [Fact]
    public void TableIsPopulatedAndWellFormed()
    {
        Assert.NotEmpty(ExpBonusItems.All);
        Assert.All(ExpBonusItems.All, item =>
        {
            Assert.NotEqual(0u, item.ItemId);
            Assert.NotEmpty(item.Name);
            Assert.NotEmpty(item.Bonus);
        });
    }

    [Fact]
    public void NoDuplicateItemIds()
    {
        Assert.Equal(ExpBonusItems.ItemIds.Count, ExpBonusItems.ItemIds.Distinct().Count());
    }

    [Theory]
    [InlineData(14043u)] // Brand-new Ring
    [InlineData(33648u)] // Menphina's Earring
    [InlineData(41081u)] // Azeyma's Earrings
    public void KnownExpGear_IsRecognised(uint itemId)
    {
        Assert.True(ExpBonusItems.Contains(itemId));
        Assert.NotEmpty(ExpBonusItems.BonusFor(itemId));
    }

    [Theory]
    [InlineData(4414u)]  // Menphina's RING — a different item with no EXP bonus
    [InlineData(19185u)] // Ala Mhigan Earrings OF AIMING — ordinary gear
    public void SimilarlyNamedGear_IsNotRecognised(uint itemId)
    {
        Assert.False(ExpBonusItems.Contains(itemId));
        Assert.Empty(ExpBonusItems.BonusFor(itemId));
    }

    [Fact]
    public void ExpGear_SurvivesCleanupOnceSeeded()
    {
        var armoury = ExpBonusItems.ItemIds
            .Select((id, i) => new ArmouryItem(id, $"exp {id}", 3200, (short)i))
            .Append(new ArmouryItem(999, "junk", 3200, 99))
            .ToList();

        var plan = ArmouryCleanupPlanner.Plan(armoury, [100u], ExpBonusItems.ItemIds);

        Assert.Equal(999u, Assert.Single(plan).ItemId);
    }

    // --- Config migration ---

    [Fact]
    public void Migration_SeedsTheKeepListWithExpGear()
    {
        var config = new CharonConfig();

        Assert.True(config.Migrate());
        Assert.All(ExpBonusItems.ItemIds, id => Assert.Contains(id, config.GearNeverEvictItemIds));
        Assert.Equal(CharonConfig.CurrentVersion, config.Version);
    }

    [Fact]
    public void Migration_RunsOnlyOnce_SoAnUntickStays()
    {
        var config = new CharonConfig();
        config.Migrate();

        // The user deliberately un-protects one — a later load must not resurrect it.
        config.GearNeverEvictItemIds.Remove(14043u);

        Assert.False(config.Migrate());
        Assert.DoesNotContain(14043u, config.GearNeverEvictItemIds);
    }

    [Fact]
    public void Migration_PreservesExistingKeepEntries()
    {
        var config = new CharonConfig();
        config.GearNeverEvictItemIds.Add(555u);

        config.Migrate();

        Assert.Contains(555u, config.GearNeverEvictItemIds);
    }

    [Fact]
    public void Migration_DoesNotDuplicateAlreadyKeptExpGear()
    {
        var config = new CharonConfig();
        config.GearNeverEvictItemIds.Add(14043u);

        config.Migrate();

        Assert.Single(config.GearNeverEvictItemIds, id => id == 14043u);
    }
}
