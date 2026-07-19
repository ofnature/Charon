using System.Numerics;
using Charon.Features.Follow;

namespace Charon.Tests.Features.Follow;

public sealed class FollowManagerTests
{
    private static FollowConfig Config(float distance = 2.5f, bool stopInBoss = true) =>
        new(distance, stopInBoss);

    private static readonly Vector3 Origin = Vector3.Zero;
    private static Vector3 Far => new(20f, 0f, 0f);   // well beyond distance + deadband
    private static Vector3 Near => new(1f, 0f, 0f);   // within distance

    private static FollowManager Following(FollowConfig? cfg = null, string leader = "Leader")
    {
        var m = new FollowManager(cfg ?? Config());
        m.StartFollowing(leader);
        return m;
    }

    [Fact]
    public void NotFollowing_IsIdle()
    {
        var m = new FollowManager(Config());
        var d = m.Evaluate(Far, Origin, inCombat: false, hasActiveModule: false, localBusy: false);
        Assert.Equal(FollowAction.Idle, d.Action);
        Assert.False(m.Following);
    }

    [Fact]
    public void FarFromLeader_Moves_ToLeader()
    {
        var m = Following();
        var d = m.Evaluate(Far, Origin, false, false, false);
        Assert.Equal(FollowAction.Move, d.Action);
        Assert.Equal(Far, d.Target);
    }

    [Fact]
    public void WithinDistance_Holds()
    {
        var m = Following();
        var d = m.Evaluate(Near, Origin, false, false, false);
        Assert.Equal(FollowAction.Hold, d.Action);
    }

    [Fact]
    public void Deadband_PreventsTwitch_JustPastDistance()
    {
        var m = Following(Config(distance: 2.5f));
        // 3.5y away: past FollowDistance (2.5) but within the 1.5 deadband → still Hold.
        var d = m.Evaluate(new Vector3(3.5f, 0f, 0f), Origin, false, false, false);
        Assert.Equal(FollowAction.Hold, d.Action);
    }

    // --- The boss-fight gate: BOTH in-combat AND module (per user) ---

    [Fact]
    public void BossFight_BothTrue_Holds()
    {
        var m = Following();
        var d = m.Evaluate(Far, Origin, inCombat: true, hasActiveModule: true, localBusy: false);
        Assert.Equal(FollowAction.Hold, d.Action);
        Assert.Contains("boss fight", d.Status);
    }

    [Fact]
    public void CombatWithoutModule_KeepsFollowing()
    {
        var m = Following();
        var d = m.Evaluate(Far, Origin, inCombat: true, hasActiveModule: false, localBusy: false);
        Assert.Equal(FollowAction.Move, d.Action); // trash pull — keep following
    }

    [Fact]
    public void ModuleWithoutCombat_KeepsFollowing()
    {
        var m = Following();
        var d = m.Evaluate(Far, Origin, inCombat: false, hasActiveModule: true, localBusy: false);
        Assert.Equal(FollowAction.Move, d.Action); // pre-pull in the arena — keep following
    }

    [Fact]
    public void BossFightGate_ClearsWhenCombatEnds_ResumesMove()
    {
        var m = Following();
        Assert.Equal(FollowAction.Hold, m.Evaluate(Far, Origin, true, true, false).Action);
        // Boss dies → combat drops (module may still be briefly loaded) → resume, no reissue.
        Assert.Equal(FollowAction.Move, m.Evaluate(Far, Origin, false, true, false).Action);
    }

    [Fact]
    public void BossFightGate_Disabled_FollowsThroughEverything()
    {
        var m = Following(Config(stopInBoss: false));
        var d = m.Evaluate(Far, Origin, inCombat: true, hasActiveModule: true, localBusy: false);
        Assert.Equal(FollowAction.Move, d.Action);
    }

    // --- Other gates ---

    [Fact]
    public void LeaderNotResolvable_Holds_WithWaitingStatus()
    {
        var m = Following(leader: "Styx");
        var d = m.Evaluate(leaderPos: null, Origin, false, false, false);
        Assert.Equal(FollowAction.Hold, d.Action);
        Assert.Contains("not in zone", d.Status);
    }

    [Fact]
    public void LocalBusy_Holds()
    {
        var m = Following();
        var d = m.Evaluate(Far, Origin, false, false, localBusy: true);
        Assert.Equal(FollowAction.Hold, d.Action);
    }

    // --- Portal / unreachable-leader handling ---

    [Fact]
    public void UnreachableLeader_Holds_InsteadOfPathingAtAWall()
    {
        var m = Following(leader: "Styx");
        var d = m.Evaluate(Far, Origin, false, false, false, leaderReachable: false);
        Assert.Equal(FollowAction.Hold, d.Action);
        Assert.Contains("unreachable", d.Status);
    }

    [Fact]
    public void LeaderBecomesReachableAgain_ResumesMove()
    {
        var m = Following();
        Assert.Equal(FollowAction.Hold, m.Evaluate(Far, Origin, false, false, false, leaderReachable: false).Action);
        // Came back through the portal (or we teleported to them) — resume, no reissue.
        Assert.Equal(FollowAction.Move, m.Evaluate(Far, Origin, false, false, false, leaderReachable: true).Action);
    }

    [Fact]
    public void NoteLeaderPosition_DetectsTeleportJump()
    {
        var m = Following();
        Assert.False(m.NoteLeaderPosition(Origin));                       // first sighting
        Assert.False(m.NoteLeaderPosition(new Vector3(5f, 0f, 0f)));      // walked
        Assert.True(m.NoteLeaderPosition(new Vector3(500f, 0f, 0f)));     // portal
    }

    [Fact]
    public void NoteLeaderPosition_LeaderVanishing_IsNotAJump()
    {
        var m = Following();
        m.NoteLeaderPosition(Origin);
        Assert.False(m.NoteLeaderPosition(null)); // out of range ≠ teleport
    }

    [Fact]
    public void PortalHint_RecordsWhereLeaderStoodBeforeJumping()
    {
        var m = Following();
        m.NoteLeaderPosition(Origin);
        var atPortal = new Vector3(10f, 0f, 0f);
        m.NoteLeaderPosition(atPortal);          // walked to the portal
        Assert.Null(m.PortalHint);

        m.NoteLeaderPosition(new Vector3(800f, 0f, 0f)); // used it
        Assert.Equal(atPortal, m.PortalHint);    // hint = the spot they clicked from
    }

    [Fact]
    public void PortalHint_ClearedExplicitlyAndOnSessionChange()
    {
        var m = Following();
        m.NoteLeaderPosition(Origin);
        m.NoteLeaderPosition(new Vector3(800f, 0f, 0f));
        Assert.NotNull(m.PortalHint);

        m.ClearPortalHint();
        Assert.Null(m.PortalHint);

        m.NoteLeaderPosition(Origin);
        m.NoteLeaderPosition(new Vector3(800f, 0f, 0f));
        m.Stop();
        Assert.Null(m.PortalHint);
    }

    [Fact]
    public void NoteLeaderPosition_ResetsOnStartAndStop()
    {
        var m = Following();
        m.NoteLeaderPosition(Origin);
        m.Stop();
        m.StartFollowing("Someone Else");
        // First sighting after a fresh session must not read as a jump.
        Assert.False(m.NoteLeaderPosition(new Vector3(900f, 0f, 0f)));
    }

    [Fact]
    public void Stop_ClearsSession()
    {
        var m = Following();
        m.Stop();
        Assert.False(m.Following);
        Assert.Equal(FollowAction.Idle, m.Evaluate(Far, Origin, false, false, false).Action);
    }

    [Fact]
    public void StartFollowing_TrimsAndStores()
    {
        var m = new FollowManager(Config());
        m.StartFollowing("  Korha Ishere  ");
        Assert.True(m.Following);
        Assert.Equal("Korha Ishere", m.LeaderName);
    }
}
