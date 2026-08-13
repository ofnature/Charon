using System;

namespace Charon.Features.Leveling;

/// <summary>
/// The shared split-quantity arithmetic for both gil-target modules: the free trial's 300k
/// personal cap (module 4) and the Doman Enclave weekly donation budget (module 5). Same
/// question in both — "given a unit value, a remaining headroom and a stack I hold, what exact
/// quantity do I sell/donate" — so it lives once, here. Pure logic, no Dalamud types.
///
/// MEET OR EXCEED, never fall short (ceil, not floor — per user decision in the plan): selling
/// past the free trial's gil cap just prints a chat line, nothing to dismiss, so overshooting by
/// one item costs nothing while undershooting leaves value on the table and costs another trip.
/// </summary>
public static class StackSplitCalculator
{
    /// <summary>
    /// Smallest quantity whose value meets or exceeds <paramref name="remainingHeadroom"/>,
    /// clamped to <paramref name="held"/>. 0 when there is nothing to do (no headroom, no
    /// value, or nothing held) — holding less than the target needs simply sells everything,
    /// which is as close as this toon can get this trip.
    /// </summary>
    public static int QuantityToReach(long remainingHeadroom, long unitValue, int held)
    {
        if (remainingHeadroom <= 0 || unitValue <= 0 || held <= 0)
            return 0;

        var needed = (remainingHeadroom + unitValue - 1) / unitValue; // ceil
        return (int)Math.Min(needed, held);
    }
}
