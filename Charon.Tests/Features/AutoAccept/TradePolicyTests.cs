using Charon.Features.AutoAccept;

namespace Charon.Tests.Features.AutoAccept;

public sealed class TradePolicyTests
{
    private static TradeAction Decide(
        bool enabled = true,
        bool trusted = true,
        TradePhase local = TradePhase.SelectingGoods,
        TradePhase remote = TradePhase.LockedIn,
        bool confirmOpen = false)
        => TradePolicy.Decide(enabled, trusted, local, remote, confirmOpen);

    // --- Mirroring the partner's lock-in ---

    [Fact]
    public void PartnerLockedIn_WeLockIn()
    {
        Assert.Equal(TradeAction.LockIn, Decide());
    }

    [Fact]
    public void PartnerStillChoosing_WeWait()
    {
        // Never commit before the partner does.
        Assert.Equal(TradeAction.None, Decide(remote: TradePhase.SelectingGoods));
    }

    [Fact]
    public void AlreadyLockedIn_DoesNotClickAgain()
    {
        Assert.Equal(TradeAction.None, Decide(local: TradePhase.LockedIn, confirmOpen: false));
    }

    // --- Confirmation ---

    [Fact]
    public void ConfirmDialogOpen_AfterLockIn_Confirms()
    {
        Assert.Equal(TradeAction.Confirm, Decide(local: TradePhase.LockedIn, confirmOpen: true));
        Assert.Equal(TradeAction.Confirm, Decide(local: TradePhase.WaitingForConfirmation, confirmOpen: true));
    }

    [Fact]
    public void ConfirmDialogOpen_BeforeWeLockedIn_DoesNothing()
    {
        // A yes/no window during goods selection is not ours to answer.
        Assert.Equal(TradeAction.None,
            TradePolicy.Decide(true, true, TradePhase.SelectingGoods, TradePhase.SelectingGoods, confirmDialogOpen: true));
    }

    // --- The trust gate (the safety property) ---

    [Fact]
    public void UntrustedPartner_NeverActs()
    {
        Assert.Equal(TradeAction.None, Decide(trusted: false));
        Assert.Equal(TradeAction.None, Decide(trusted: false, local: TradePhase.LockedIn, confirmOpen: true));
    }

    [Fact]
    public void Disabled_NeverActs()
    {
        Assert.Equal(TradeAction.None, Decide(enabled: false));
        Assert.Equal(TradeAction.None, Decide(enabled: false, local: TradePhase.LockedIn, confirmOpen: true));
    }

    [Fact]
    public void NotTrading_DoesNothing()
    {
        Assert.Equal(TradeAction.None, Decide(local: TradePhase.NotTrading, remote: TradePhase.NotTrading));
    }

    [Fact]
    public void RequestPending_IsLeftToThePlayer()
    {
        // The opening request is not auto-accepted — the partner isn't verifiable yet.
        Assert.Equal(TradeAction.None, Decide(local: TradePhase.RequestPending, remote: TradePhase.RequestPending));
    }
}
