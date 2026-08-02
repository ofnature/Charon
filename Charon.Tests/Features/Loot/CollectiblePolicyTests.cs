using Charon.Features.Loot;

namespace Charon.Tests.Features.Loot;

public sealed class CollectiblePolicyTests
{
    private static CollectibleItem Item(
        uint id, string name = "thing", string category = "Minion",
        uint kind = CollectibleKinds.Minion,
        bool unlocked = false, int container = 0, short slot = 0) =>
        new(id, name, category, kind, unlocked, container, slot);

    [Fact]
    public void UnlearnedItems_AreListed()
    {
        var rows = CollectiblePolicy.Unlearned([Item(1, "Wind-up Sun")]);
        Assert.Equal("Wind-up Sun", Assert.Single(rows).Name);
    }

    [Fact]
    public void AlreadyUnlockedItems_AreHidden()
    {
        // A duplicate cannot be relearned, so it is not actionable — and on a paid toon it stays
        // sellable precisely because nothing here consumes it.
        var rows = CollectiblePolicy.Unlearned([Item(1, unlocked: true)]);
        Assert.Empty(rows);
    }

    [Fact]
    public void EmptySlots_AreIgnored()
    {
        Assert.Empty(CollectiblePolicy.Unlearned([Item(0)]));
    }

    [Fact]
    public void MixedBag_KeepsOnlyTheUnlearned()
    {
        var rows = CollectiblePolicy.Unlearned([
            Item(1, "Owned Mount", unlocked: true),
            Item(2, "New Mount"),
            Item(3, "Owned Card", unlocked: true),
        ]);

        Assert.Equal(2u, Assert.Single(rows).ItemId);
    }

    [Fact]
    public void OrderIsStable_ByCategoryThenName()
    {
        var rows = CollectiblePolicy.Unlearned([
            Item(3, "Zeta", "Minion"),
            Item(1, "Alpha", "Mount"),
            Item(2, "Beta", "Minion"),
        ]);

        Assert.Equal(["Beta", "Zeta", "Alpha"], rows.Select(r => r.Name));
    }

    [Fact]
    public void SameNameDifferentSlots_BothListed_AndOrderedBySlot()
    {
        var rows = CollectiblePolicy.Unlearned([
            Item(1, "Faded Copy", "Miscellany", slot: 5),
            Item(1, "Faded Copy", "Miscellany", slot: 2),
        ]);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].Slot);
    }

    // --- The allowlist: unlock state alone is NOT enough ---

    [Fact]
    public void OrdinaryConsumables_AreNeverListed()
    {
        // The bug this exists for: IsItemActionUnlocked reports a Potion as "not unlocked" exactly
        // like a genuinely unlearned collectible, so 21 potions and an aetheryte ticket were offered
        // for collection. Clicking one would have drunk it.
        var rows = CollectiblePolicy.Unlearned([
            Item(1, "Hi-Potion of Strength", "Medicine", kind: 1),
            Item(2, "Vesper Bay Aetheryte Ticket", "Other", kind: 2),
            Item(3, "Gysahl Greens", "Miscellany", kind: 3),
        ]);

        Assert.Empty(rows);
    }

    [Fact]
    public void KnownKinds_AreListed()
    {
        var rows = CollectiblePolicy.Unlearned([
            Item(1, "Wind-up Sun", "Minion", CollectibleKinds.Minion),
            Item(2, "Homestead Orchestrion Roll", "Orchestrion Roll", CollectibleKinds.OrchestrionRoll),
            Item(3, "Modern Aesthetics - Curls", "Miscellany", CollectibleKinds.EmoteOrHairstyle),
            Item(4, "Chocobo Whistle", "Mount", CollectibleKinds.Mount),
        ]);

        Assert.Equal(4, rows.Count);
    }

    [Fact]
    public void UnknownKinds_AreReportedOncePerKind()
    {
        var unknown = CollectiblePolicy.UnknownKinds([
            Item(1, "Potion", "Medicine", kind: 99),
            Item(2, "Potion", "Medicine", kind: 99),
            Item(3, "Something Unknown", "Miscellany", kind: 987654),
            Item(4, "Wind-up Sun", "Minion", CollectibleKinds.Minion),
        ]);

        Assert.Equal(2, unknown.Count);
        Assert.DoesNotContain(unknown, u => u.ActionKind == CollectibleKinds.Minion);
    }

    [Fact]
    public void EmotesAndHairstyles_ShareOneKind_AndBothQualify()
    {
        // 2633 is a generic unlock link: Ballroom Etiquette (emote) and Modern Aesthetics
        // (hairstyle) carry the identical value AND the identical UI category, so nothing available
        // here can separate them. Per-type filters for those two are impossible.
        var rows = CollectiblePolicy.Unlearned([
            Item(1, "Ballroom Etiquette - Ultima", "Miscellany", CollectibleKinds.EmoteOrHairstyle),
            Item(2, "Modern Aesthetics - Curls", "Miscellany", CollectibleKinds.EmoteOrHairstyle),
        ]);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void TriadCardPacks_StayExcluded()
    {
        // 2120 items are booster PACKS that open into random cards, not one-time unlocks, so
        // "already collected" is not even a meaningful question for them.
        var rows = CollectiblePolicy.Unlearned([Item(1, "Gold Triad Card", "Miscellany", kind: 2120)]);
        Assert.Empty(rows);
    }

    // --- Zone-restricted kinds: listed everywhere, collectable only where they work ---

    [Fact]
    public void PhantomJobShard_IsListed_SoYouCanSeeYouHoldOne()
    {
        var rows = CollectiblePolicy.Unlearned([
            Item(1, "Mystic Knight's Soul Shard", "Miscellany", CollectibleKinds.PhantomJobShard),
        ]);

        Assert.Single(rows);
    }

    [Theory]
    [InlineData(1252u, true)]   // South Horn
    [InlineData(1346u, true)]   // North Horn
    [InlineData(478u, false)]   // Idyllshire — anywhere else
    public void PhantomJobShard_IsOnlyCollectableInTheOccultCrescent(uint territory, bool expected)
    {
        var shard = Item(1, "Mystic Knight's Soul Shard", "Miscellany", CollectibleKinds.PhantomJobShard);
        Assert.Equal(expected, CollectiblePolicy.CanCollectHere(shard, territory));
    }

    [Fact]
    public void OccultRecordNotes_AreListedAndCollectableAnywhere()
    {
        // Adjacent to the shard kind and also Occult Crescent content, but its tooltip carries no
        // "only on the Occult Crescent" line — so it is deliberately NOT zone-gated.
        var note = Item(1, "Notes on the Cloister Demon", "Miscellany", CollectibleKinds.OccultRecordNote);

        Assert.Single(CollectiblePolicy.Unlearned([note]));
        Assert.True(CollectiblePolicy.CanCollectHere(note, 478u));
    }

    [Fact]
    public void OrdinaryKinds_AreCollectableAnywhere()
    {
        var minion = Item(1, "Wind-up Sun", "Minion", CollectibleKinds.Minion);
        Assert.True(CollectiblePolicy.CanCollectHere(minion, 478u));
    }

    [Fact]
    public void UnknownKinds_IncludeAlreadyUnlocked_SoTheMappingIsLearnedFromEverything()
    {
        var unknown = CollectiblePolicy.UnknownKinds([Item(1, kind: 4242, unlocked: true)]);
        Assert.Equal(4242u, Assert.Single(unknown).ActionKind);
    }
}
