using System.Collections.Generic;
using System.Linq;
using Charon.Features.FcChest;

namespace Charon.Tests.Features.FcChest;

public sealed class SaddlebagDuplicatesTests
{
    private static SaddlebagCandidate Stack(
        uint itemId, int container = 0, short slot = 0, int quantity = 1, bool unique = false) =>
        new(itemId, container, slot, quantity, unique);

    private static readonly IReadOnlySet<uint> Saddle = new HashSet<uint> { 10, 20 };

    [Fact]
    public void OnlyItemsTheSaddlebagAlreadyHolds_AreEntrusted()
    {
        // The duplicates-only doctrine, same as the FC chest: the saddlebag copy is the seed.
        // An item the saddlebag has never seen is never moved in.
        var plan = SaddlebagDuplicates.FindEntrustable(
            [Stack(10), Stack(99), Stack(20)], Saddle);

        Assert.Equal(new uint[] { 10, 20 }, plan.Select(p => p.ItemId));
    }

    [Fact]
    public void UniqueItems_AreNeverEntrusted()
    {
        // Pandora entrusts these too and the game then refuses (unique = one copy anywhere);
        // skipping them outright is the fix, not a behaviour difference worth keeping.
        var plan = SaddlebagDuplicates.FindEntrustable([Stack(10, unique: true)], Saddle);
        Assert.Empty(plan);
    }

    [Fact]
    public void EmptySlots_AreIgnored()
    {
        Assert.Empty(SaddlebagDuplicates.FindEntrustable([Stack(0)], Saddle));
    }

    [Fact]
    public void OrderIsStable_ByContainerThenSlot()
    {
        // The executor takes the FIRST entry each round, so the plan must be deterministic —
        // the same bags always yield the same next move.
        var plan = SaddlebagDuplicates.FindEntrustable(
            [Stack(10, container: 2, slot: 5), Stack(20, container: 1, slot: 9), Stack(10, container: 1, slot: 2)],
            Saddle);

        Assert.Equal(new short[] { 2, 9, 5 }, plan.Select(p => p.Slot));
    }

    [Fact]
    public void NothingInTheSaddlebag_MeansNothingToEntrust()
    {
        Assert.Empty(SaddlebagDuplicates.FindEntrustable([Stack(10), Stack(20)], new HashSet<uint>()));
    }
}
