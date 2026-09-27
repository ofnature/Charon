using System;

namespace Charon.Features.Retainers;

/// <summary>
/// Matching for retainer-menu entries, whose Addon-sheet text carries RUNTIME PLACEHOLDERS.
///
/// Row 2378 reads <c>"Entrust or withdraw items. (Slots filled: 0)"</c> in the sheet and
/// <c>"... (Slots filled: 172)"</c> on screen; 2379 does the same with gil. Exact equality can
/// never match those, which is why a selector that used it reported "waiting for a bell" while
/// standing in the retainer's own menu. Comparing the STEM — everything before the first
/// parenthesis — matches the wording without depending on the number the game substitutes.
///
/// This is deliberately NOT applied to the venture menu. Row 2385 is
/// <c>"View venture report. (Complete)"</c> and matches exactly only while a venture really is
/// complete — on screen an unfinished one reads <c>"(Complete on 9/27 8:11)"</c> — so exact
/// equality there is doing useful work, and loosening it would open the report of a venture that
/// has not finished. Per row, from evidence: some suffixes are data, others are state.
/// </summary>
public static class MenuEntryText
{
    /// <summary>Everything before the first "(", trimmed. Text without one is returned as-is.</summary>
    public static string Stem(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cut = text.IndexOf('(');
        return (cut < 0 ? text : text[..cut]).Trim();
    }

    /// <summary>
    /// Whether a live menu entry is the sheet row's entry, ignoring any substituted values. Blank
    /// text never matches, so an unreadable sheet row can only ever do nothing.
    /// </summary>
    public static bool Matches(string? entry, string? sheetText)
    {
        var wanted = Stem(sheetText);
        return wanted.Length > 0 && string.Equals(Stem(entry), wanted, StringComparison.OrdinalIgnoreCase);
    }
}
