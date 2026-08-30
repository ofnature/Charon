using System.Linq;
using Charon.Features.DeepDungeon;

namespace Charon.Tests.Features.DeepDungeon;

public sealed class MobDatabaseTests
{
    // The exact shape of NecroLens's allMobs.json (MIT), including its integer enums.
    private const string Sample =
        "[{\"Id\":4578,\"AggroType\":2,\"DangerLevel\":0,\"Patrol\":false,\"BossOrAdd\":true,\"Special\":false},"
        + "{\"Id\":4975,\"AggroType\":0,\"DangerLevel\":1,\"Patrol\":true,\"BossOrAdd\":false,\"Special\":false},"
        + "{\"Id\":5000,\"AggroType\":1,\"DangerLevel\":2,\"Patrol\":false,\"BossOrAdd\":false,\"Special\":true}]";

    [Fact]
    public void ParsesTheNecroLensShape()
    {
        var rows = MobDatabase.ParseForTest(Sample);

        Assert.Equal(3, rows.Count);
        Assert.Equal(MobAggro.Proximity, rows[0].Aggro); // 2 = proximity, per the enum order
        Assert.Equal(MobAggro.Sight, rows[1].Aggro);     // 0 = sight
        Assert.Equal(MobAggro.Sound, rows[2].Aggro);     // 1 = sound
        Assert.True(rows[1].Patrol);
        Assert.True(rows[0].BossOrAdd);
        Assert.True(rows[2].Special);
    }

    [Fact]
    public void OutOfRangeAggroValues_ClampInsteadOfThrowing()
    {
        var rows = MobDatabase.ParseForTest(
            "[{\"Id\":1,\"AggroType\":9,\"DangerLevel\":0,\"Patrol\":false,\"BossOrAdd\":false,\"Special\":false}]");

        Assert.Equal(MobAggro.Proximity, rows.Single().Aggro);
    }

    [Fact]
    public void EmbeddedDataset_LoadsAndIsKeyedByNameId()
    {
        // The real resource: ~700 curated mobs. 4578 is the first row of the shipped file, so
        // a lookup miss here means the embedding or the key (NameId) broke.
        var db = new MobDatabase();

        Assert.True(db.Count > 600, $"expected the full dataset, got {db.Count}");
        Assert.NotNull(db.Find(4578));
        Assert.Null(db.Find(1));
    }
}
