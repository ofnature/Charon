namespace Charon.Features.Leveling;

/// <summary>Which of the two Doman donation steps the UI should offer.</summary>
public enum DomanStep
{
    /// <summary>Read the budget, close the basket (the game blocks splits while it is open), split the exact stack.</summary>
    Prepare,

    /// <summary>Basket reopened with the stack ready: donate it, press Donate, answer the confirmation.</summary>
    Stage,
}

/// <summary>
/// What the Doman UI shows: <see cref="Primary"/> is the step whose turn it is, the two flags say which
/// steps are already done, <see cref="CanPrepare"/> / <see cref="CanStage"/> are the two buttons' own
/// gates (they are not the same button), and <see cref="Reason"/> explains a dead button instead of
/// leaving a disabled control with no story.
/// </summary>
public readonly record struct DomanStepPlan(
    DomanStep Primary,
    bool StepOneDone,
    bool StepTwoDone,
    bool CanPrepare,
    bool CanStage,
    string Reason);

/// <summary>
/// The Doman flow's gating, as pure logic. The flow is two-phase and each phase has a different
/// precondition: Prepare needs the basket OPEN to read the budget and then CLOSES it on purpose (the game
/// blocks inventory splits while it is open), and Stage needs it open again. That is why step 1 finishing
/// has to be remembered at all — nothing on screen would otherwise say the split already happened.
/// </summary>
public static class DomanStepPolicy
{
    /// <summary>Nothing is open yet — the basket window IS the session.</summary>
    public const string BasketClosed = "open the donation basket — the window is the session";

    /// <summary>The stack is split and waiting; the basket has to come back before Stage can run.</summary>
    public const string ReopenForStage = "reopen the basket, then Stage the prepared stack";

    /// <summary>Nothing in the bags is at or under the target, so there is nothing to stage yet.</summary>
    public const string NothingStageable = "run Prepare first — nothing stageable in the bags";

    /// <summary>The week is done; a spent basket refuses to open at all.</summary>
    public const string WeekSpent = "this week's budget is spent";

    /// <summary>Mid-operation: the window shows Stop instead of a step.</summary>
    public const string Running = "running";

    /// <summary>
    /// Compose the plan.
    /// </summary>
    /// <param name="basketOpen">The game's donation basket window is up (<c>WindowSnapshot.Open</c>).</param>
    /// <param name="busy">An operation is running (<c>DomanDonator.Busy</c>).</param>
    /// <param name="stackReady">A verified split stack is in the bags (<c>DomanDonator.StackReady</c>).</param>
    /// <param name="held">How many of the donation item the toon is carrying.</param>
    /// <param name="stageable">
    /// There is something Stage may hand over: the prepared stack, or a holding that is already at or
    /// under the target (holding less than the target means everything held IS the donation).
    /// </param>
    /// <param name="budgetRemaining">Weekly budget left, or -1 when nothing could read it.</param>
    /// <param name="itemName">The configured donation item's name, for the "none in the bags" message.</param>
    public static DomanStepPlan Compose(
        bool basketOpen,
        bool busy,
        bool stackReady,
        int held,
        bool stageable,
        long budgetRemaining,
        string itemName)
    {
        var spent = budgetRemaining == 0;
        var stepOneDone = stackReady || spent;
        var stepTwoDone = spent;
        var primary = stackReady ? DomanStep.Stage : DomanStep.Prepare;

        if (busy)
            return new DomanStepPlan(primary, stepOneDone, stepTwoDone, false, false, Running);

        // A spent week is the one state where no button ever makes sense: there is nothing to donate and
        // the basket will not even open. It is also what "delivered" looks like a moment later.
        if (spent)
            return new DomanStepPlan(primary, true, true, false, false, WeekSpent);

        if (!basketOpen)
            return new DomanStepPlan(primary, stepOneDone, stepTwoDone, false, false,
                stackReady ? ReopenForStage : BasketClosed);

        if (held <= 0)
            return new DomanStepPlan(primary, stepOneDone, stepTwoDone, false, false, $"no {itemName} in the bags");

        // Basket open with the item in hand: Prepare is always allowed (it is idempotent — it finds an
        // already-ready stack and just re-marks it), and Stage unlocks once there is something to stage.
        var reason = !stageable && primary == DomanStep.Stage ? NothingStageable : string.Empty;
        return new DomanStepPlan(primary, stepOneDone, stepTwoDone, true, stageable, reason);
    }
}
