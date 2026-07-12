namespace Charon.Features.HealWatch;

/// <summary>What the local healer can do for Heal Watch. Zeroed slots = capability missing.</summary>
/// <param name="HealAction">Single-target GCD heal.</param>
/// <param name="HotAction">HoT/shield to maintain (WHM Regen / AST Aspected Benefic / SCH Adloquium).</param>
/// <param name="HotStatusId">Status the HoT applies — checked on the TARGET before recasting
/// (same doctrine as Daedalus DoT upkeep: never reapply while the status is still running).</param>
/// <param name="RaiseAction">Hardcast raise (no swiftcast needed for parked bots).</param>
public sealed record HealerKit(uint HealAction, uint HotAction, uint HotStatusId, uint RaiseAction);

/// <summary>
/// Per-job Heal Watch kit by level. Deliberately NOT a rotation — one dependable heal, one
/// maintainable HoT/shield, one raise per job; Daedalus owns anything smarter. Pure and testable.
/// </summary>
public static class HealActionTable
{
    // ClassJob row ids
    public const uint Conjurer = 6;
    public const uint WhiteMage = 24;
    public const uint Scholar = 28;
    public const uint Astrologian = 33;
    public const uint Sage = 40;

    // Heals
    private const uint Cure = 120;        // CNJ/WHM Lv2
    private const uint CureII = 135;      // WHM Lv30
    private const uint Physick = 190;     // SCH Lv4
    private const uint Benefic = 3594;    // AST Lv2
    private const uint BeneficII = 3610;  // AST Lv26
    private const uint Diagnosis = 24284; // SGE Lv2

    // HoTs / shields (+ the status they leave on the target)
    private const uint Regen = 137;            // WHM Lv35
    private const uint RegenStatus = 158;
    private const uint Adloquium = 185;        // SCH Lv30 (Galvanize shield)
    private const uint GalvanizeStatus = 297;
    private const uint AspectedBenefic = 3595; // AST Lv34 (HoT)
    private const uint AspectedBeneficStatus = 835;

    // Raises (all Lv12; hardcast is fine for Heal Watch)
    private const uint Raise = 125;        // CNJ/WHM
    private const uint Resurrection = 173; // SCH
    private const uint Ascend = 3603;      // AST
    private const uint Egeiro = 24287;     // SGE

    /// <summary>True when the job can heal others at all (the Heal Watch job gate).</summary>
    public static bool IsHealerJob(uint classJobId) =>
        classJobId is Conjurer or WhiteMage or Scholar or Astrologian or Sage;

    /// <summary>The job's Heal Watch kit at this level; null when not a healer.</summary>
    public static HealerKit? GetKit(uint classJobId, int level) => classJobId switch
    {
        Conjurer => new HealerKit(
            level >= 2 ? Cure : 0,
            0, 0, // CNJ has no HoT
            level >= 12 ? Raise : 0),
        WhiteMage => new HealerKit(
            level >= 30 ? CureII : level >= 2 ? Cure : 0,
            level >= 35 ? Regen : 0, level >= 35 ? RegenStatus : 0,
            level >= 12 ? Raise : 0),
        Scholar => new HealerKit(
            level >= 4 ? Physick : 0,
            level >= 30 ? Adloquium : 0, level >= 30 ? GalvanizeStatus : 0,
            level >= 12 ? Resurrection : 0),
        Astrologian => new HealerKit(
            level >= 26 ? BeneficII : level >= 2 ? Benefic : 0,
            level >= 34 ? AspectedBenefic : 0, level >= 34 ? AspectedBeneficStatus : 0,
            level >= 12 ? Ascend : 0),
        Sage => new HealerKit(
            level >= 2 ? Diagnosis : 0,
            0, 0, // Eukrasian Diagnosis needs the Eukrasia two-step — out of scope for v1
            level >= 12 ? Egeiro : 0),
        _ => null,
    };
}
