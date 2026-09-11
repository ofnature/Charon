using System;

namespace Charon.Features.Gear;

/// <summary>
/// Reading a ClassJobCategory's NAME as a job list — the fallback for jobs the sheet has no
/// boolean column for.
///
/// The sheet normally stores one bool column per job, named by abbreviation, and that is what
/// <c>GearManager.FitsJob</c> reads. BEASTMASTER has no such column (verified against the live
/// schema: "All Classes" lists ACN through WVR and BST is simply absent), so the reflective
/// lookup finds nothing and every piece of gear reads as unwearable — which is exactly why a BST
/// toon was shown no upgrades at all. The category NAMES do know about it though: rows like
/// "PGL MNK SAM BST" and "PGL LNC MNK DRG SAM RPR BST" name the job outright, so for a
/// column-less job the name is the better evidence.
/// </summary>
public static class JobCategoryName
{
    /// <summary>
    /// Whether the name is a LIST OF JOBS ("PGL MNK SAM BST") rather than a description
    /// ("All Classes", "Disciple of War"). A job list is every token being a three-letter
    /// uppercase code, which is the shape the game uses for these rows.
    /// </summary>
    public static bool IsJobList(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var any = false;
        foreach (var token in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsJobCode(token))
                return false;
            any = true;
        }

        return any;
    }

    /// <summary>Whether a job-list name names this job. Whole tokens only — BST is not BSM.</summary>
    public static bool Mentions(string? name, string? abbreviation)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(abbreviation))
            return false;

        foreach (var token in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals(abbreviation, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsJobCode(string token)
    {
        if (token.Length != 3)
            return false;

        foreach (var c in token)
        {
            if (c is < 'A' or > 'Z')
                return false;
        }

        return true;
    }
}
