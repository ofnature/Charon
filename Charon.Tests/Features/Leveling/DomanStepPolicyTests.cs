using Charon.Features.Leveling;

namespace Charon.Tests.Features.Leveling;

/// <summary>
/// The Doman flow's gates. Two buttons with different preconditions (Prepare needs the basket open and
/// then closes it; Stage needs it open AND something stageable), which is the part that turns into a
/// dead button with no explanation if it is only ever eyeballed in game.
/// </summary>
public sealed class DomanStepPolicyTests
{
    private const string Item = "Duck Bones";

    private static DomanStepPlan Plan(
        bool open = true,
        bool busy = false,
        bool ready = false,
        int held = 1204,
        bool stageable = false,
        long budget = 12600) =>
        DomanStepPolicy.Compose(open, busy, ready, held, stageable, budget, Item);

    [Fact]
    public void BasketClosed_NothingToRun_AndTheReasonSaysWhy()
    {
        var plan = Plan(open: false);

        Assert.Equal(DomanStep.Prepare, plan.Primary);
        Assert.False(plan.CanPrepare);
        Assert.False(plan.CanStage);
        Assert.Equal(DomanStepPolicy.BasketClosed, plan.Reason);
    }

    [Fact]
    public void BasketOpen_WithTheItem_PrepareIsLive_AndStageIsStillLocked()
    {
        // Nothing has been split yet: step 2 has nothing it could hand over.
        var plan = Plan(stageable: false);

        Assert.Equal(DomanStep.Prepare, plan.Primary);
        Assert.True(plan.CanPrepare);
        Assert.False(plan.CanStage);
        Assert.False(plan.StepOneDone);
        Assert.Equal(string.Empty, plan.Reason);
    }

    [Fact]
    public void PreparedStack_UnlocksStage_AndMarksStepOneDone()
    {
        var plan = Plan(ready: true, stageable: true);

        Assert.Equal(DomanStep.Stage, plan.Primary);
        Assert.True(plan.StepOneDone);
        Assert.False(plan.StepTwoDone);
        Assert.True(plan.CanPrepare);
        Assert.True(plan.CanStage);
    }

    [Fact]
    public void PreparedStack_WithTheBasketClosed_AsksForTheBasketBack()
    {
        // The state Prepare leaves behind: the stack is in the bags, the basket is shut (by design).
        var plan = Plan(open: false, ready: true, stageable: true);

        Assert.Equal(DomanStep.Stage, plan.Primary);
        Assert.True(plan.StepOneDone);
        Assert.False(plan.CanStage);
        Assert.Equal(DomanStepPolicy.ReopenForStage, plan.Reason);
    }

    [Fact]
    public void HoldingAtOrUnderTheTarget_StageIsReachableWithoutPrepare()
    {
        // Holding less than the target means the whole holding IS the donation — no split needed.
        var plan = Plan(stageable: true);

        Assert.Equal(DomanStep.Prepare, plan.Primary);
        Assert.True(plan.CanPrepare);
        Assert.True(plan.CanStage);
        Assert.Equal(string.Empty, plan.Reason);
    }

    [Fact]
    public void PreparedStackWithNothingStageable_ExplainsItself()
    {
        // Should not happen (a ready stack is stageable by definition); if it ever does, say so rather
        // than showing a greyed-out button with no story.
        var plan = Plan(ready: true, stageable: false);

        Assert.Equal(DomanStep.Stage, plan.Primary);
        Assert.False(plan.CanStage);
        Assert.Equal(DomanStepPolicy.NothingStageable, plan.Reason);
    }

    [Fact]
    public void SpentWeek_ClosesBothSteps()
    {
        var plan = Plan(ready: true, stageable: true, budget: 0);

        Assert.True(plan.StepOneDone);
        Assert.True(plan.StepTwoDone);
        Assert.False(plan.CanPrepare);
        Assert.False(plan.CanStage);
        Assert.Equal(DomanStepPolicy.WeekSpent, plan.Reason);
    }

    [Fact]
    public void UnreadableBudget_IsNotSpent()
    {
        // -1 is "nothing could read it" — a spent week is 0 and only 0.
        var plan = Plan(budget: -1);

        Assert.True(plan.CanPrepare);
        Assert.False(plan.StepTwoDone);
    }

    [Fact]
    public void Busy_StopsBothButtons()
    {
        var plan = Plan(ready: true, stageable: true, busy: true);

        Assert.False(plan.CanPrepare);
        Assert.False(plan.CanStage);
        Assert.Equal(DomanStepPolicy.Running, plan.Reason);
        Assert.True(plan.StepOneDone); // what is already done stays done while it runs
    }

    [Fact]
    public void NoItemInTheBags_NamesTheItem()
    {
        var plan = Plan(held: 0);

        Assert.False(plan.CanPrepare);
        Assert.False(plan.CanStage);
        Assert.Contains(Item, plan.Reason);
    }
}
