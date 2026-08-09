using Charon.Ipc;

namespace Charon.Tests.Ipc;

public sealed class FollowRelayTests
{
    [Fact]
    public void StartCommand_RoundTrips()
    {
        var m = FollowRelay.Parse(FollowRelay.Serialize("Leader", "Bot", FollowRelay.ActStart));

        Assert.NotNull(m);
        Assert.Equal("Leader", m!.Leader);
        Assert.Equal("Bot", m.Target);
        Assert.Equal(FollowRelay.ActStart, m.Act);
    }

    [Fact]
    public void StartWithoutALeader_IsRejected()
    {
        // "Follow nobody" is not a command — it would leave the receiver following an empty name.
        Assert.Null(FollowRelay.Parse(FollowRelay.Serialize(string.Empty, "Bot", FollowRelay.ActStart)));
    }

    [Fact]
    public void UnknownAct_IsRejected()
    {
        Assert.Null(FollowRelay.Parse("{\"target\":\"Bot\",\"act\":\"explode\"}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"act\":\"start\"}")] // no target
    public void BadInput_ParsesToNull(string? json)
    {
        Assert.Null(FollowRelay.Parse(json));
    }

    // --- Transition announcements: the sender says where it went through ---

    [Fact]
    public void PortalAnnouncement_RoundTripsPositionAndObject()
    {
        var json = FollowRelay.SerializePortal("Leader", 12.5f, -3f, 40.25f, dataId: 2015252u);
        var m = FollowRelay.Parse(json);

        Assert.NotNull(m);
        Assert.Equal(FollowRelay.ActPortal, m!.Act);
        Assert.Equal("Leader", m.Target); // about its SENDER, like a state frame
        Assert.Equal(12.5f, m.X);
        Assert.Equal(-3f, m.Y);
        Assert.Equal(40.25f, m.Z);
        Assert.Equal(2015252u, m.DataId);
    }

    [Fact]
    public void PortalAnnouncement_SurvivesAnUnknownObject()
    {
        // DataId 0 means "something was there but we couldn't name it" — the position alone is
        // still a usable hint, so this must not be thrown away.
        var m = FollowRelay.Parse(FollowRelay.SerializePortal("Leader", 1f, 2f, 3f, dataId: 0));

        Assert.NotNull(m);
        Assert.Equal(0u, m!.DataId);
    }

    [Fact]
    public void PortalAnnouncement_NeedsASender()
    {
        // Without a name no receiver can tell whether it came from the toon they follow.
        Assert.Null(FollowRelay.Parse(FollowRelay.SerializePortal(string.Empty, 1f, 2f, 3f, 99u)));
    }

    [Fact]
    public void OlderFramesWithoutPositionFields_StillParse()
    {
        // Extend-only tolerance: a box on a previous build sends start/stop/state with no x/y/z.
        var m = FollowRelay.Parse("{\"leader\":\"Leader\",\"target\":\"Bot\",\"act\":\"state\"}");

        Assert.NotNull(m);
        Assert.Equal(0f, m!.X);
        Assert.Equal(0u, m.DataId);
    }
}
