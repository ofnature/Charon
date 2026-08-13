namespace Charon.Features.Leveling;

/// <summary>
/// Parses the Doman Enclave donation window's text nodes. Pure logic — no Dalamud types.
///
/// The window states its numbers directly (verified from a live node dump: "Weekly Budget:"
/// "25,000", "Rate" "200" "%"), so no arithmetic against a full-cap constant is ever needed —
/// but the text carries thousands separators and can carry icon glyphs, so the read strips to
/// digits rather than trusting a format.
/// </summary>
public static class DonationWindowParser
{
    /// <summary>"25,000" → 25000. Any non-digit (separators, icons, spaces) is skipped. -1 when
    /// the text carries no digits at all — 0 is a real value ("Grand Total 0"), so absence must
    /// not read as zero.</summary>
    public static long ParseAmount(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return -1;

        long value = 0;
        var sawDigit = false;
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
                continue;
            sawDigit = true;
            value = value * 10 + (c - '0');
        }

        return sawDigit ? value : -1;
    }

    /// <summary>
    /// Budget consumed per donated item — the gratuity: PriceLow × rate / 100. VERIFIED against
    /// a live basket (user-confirmed formula): 3 × 150-gil orchestrion rolls at rate 200% showed
    /// Gratuity 300 per unit and Grand Total 900 = 300 × 3. The row total is gratuity × stack
    /// size, and THAT is what counts against the weekly budget — the base value is not part of
    /// the pool.
    /// </summary>
    public static long UnitGratuity(long priceLow, long ratePercent) =>
        priceLow <= 0 || ratePercent <= 0 ? 0 : priceLow * ratePercent / 100;

    /// <summary>
    /// How many items to donate to meet-or-exceed the remaining weekly budget (over, never
    /// short — the user's rule; overshoot just wastes a little sell value, undershoot wastes a
    /// weekly trip).
    /// </summary>
    public static int TargetQuantity(long budgetRemaining, long priceLow, long ratePercent, int held) =>
        StackSplitCalculator.QuantityToReach(budgetRemaining, UnitGratuity(priceLow, ratePercent), held);
}
