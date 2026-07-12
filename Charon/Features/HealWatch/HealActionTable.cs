namespace Charon.Features.HealWatch;

/// <summary>
/// The basic single-target heal per healer job, by level. Deliberately NOT a rotation —
/// one dependable GCD heal each; Daedalus owns anything smarter. Pure and testable.
/// </summary>
public static class HealActionTable
{
    // ClassJob row ids
    public const uint Conjurer = 6;
    public const uint WhiteMage = 24;
    public const uint Scholar = 28;
    public const uint Astrologian = 33;
    public const uint Sage = 40;

    // Action row ids
    private const uint Cure = 120;        // CNJ/WHM Lv2
    private const uint CureII = 135;      // WHM Lv30
    private const uint Physick = 190;     // SCH Lv4
    private const uint Benefic = 3594;    // AST Lv2
    private const uint BeneficII = 3610;  // AST Lv26
    private const uint Diagnosis = 24284; // SGE Lv2

    /// <summary>True when the job can heal others at all (the Heal Watch job gate).</summary>
    public static bool IsHealerJob(uint classJobId) =>
        classJobId is Conjurer or WhiteMage or Scholar or Astrologian or Sage;

    /// <summary>Action id of the job's single-target heal at this level; 0 = none (not a healer / too low).</summary>
    public static uint GetHealAction(uint classJobId, int level) => classJobId switch
    {
        Conjurer when level >= 2 => Cure,
        WhiteMage when level >= 30 => CureII,
        WhiteMage when level >= 2 => Cure,
        Scholar when level >= 4 => Physick,
        Astrologian when level >= 26 => BeneficII,
        Astrologian when level >= 2 => Benefic,
        Sage when level >= 2 => Diagnosis,
        _ => 0,
    };
}
