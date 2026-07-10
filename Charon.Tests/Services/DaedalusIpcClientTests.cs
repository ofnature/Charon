using Charon.Features.AutoAccept;
using Charon.Ipc;
using Charon.Services;

namespace Charon.Tests.Services;

/// <summary>
/// Covers the testable halves of the IPC layer: tolerant JSON parsing, and graceful fallback to
/// the manual whitelist when Daedalus is not loaded (unavailable roster provider).
/// </summary>
public sealed class DaedalusIpcClientTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // --- Roster parsing ---

    [Fact]
    public void ParseRoster_ValidJson_PreservesOrderAndFields()
    {
        const string json = """
            [
                { "name": "Arthena", "world": "Ultros", "machine": "box-a", "online": true },
                { "name": "Kronos", "world": "Ultros", "machine": "box-b", "online": false }
            ]
            """;

        var roster = DaedalusIpcClient.ParseRoster(json);

        Assert.Equal(2, roster.Count);
        Assert.Equal("Arthena", roster[0].CharacterName);
        Assert.Equal("Ultros", roster[0].World);
        Assert.Equal("box-a", roster[0].MachineId);
        Assert.True(roster[0].IsOnline);
        Assert.False(roster[1].IsOnline);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"object\": \"not an array\" }")]
    [InlineData("null")]
    public void ParseRoster_BadInput_ReturnsEmpty_NeverThrows(string? json)
    {
        Assert.Empty(DaedalusIpcClient.ParseRoster(json));
    }

    [Fact]
    public void ParseRoster_DropsEntriesWithoutNames()
    {
        const string json = """[ { "name": "", "world": "Ultros" }, { "name": "Kronos" } ]""";

        var roster = DaedalusIpcClient.ParseRoster(json);

        Assert.Equal("Kronos", Assert.Single(roster).CharacterName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void ParseTrustList_BadInput_ReturnsEmpty(string? json)
    {
        Assert.Empty(DaedalusIpcClient.ParseTrustList(json));
    }

    [Fact]
    public void ParseTrustList_ValidJson_DropsBlankNames()
    {
        var list = DaedalusIpcClient.ParseTrustList("""[ "Arthena", "", "Kronos" ]""");
        Assert.Equal(new[] { "Arthena", "Kronos" }, list);
    }

    // --- Pillion assignment message parsing ---

    [Fact]
    public void PillionAssignment_ValidJson_Parses()
    {
        var message = CharonPillionIpc.Parse(
            """{ "owner": "Arthena", "member": "Kronos", "seat": 3 }""");

        Assert.NotNull(message);
        Assert.Equal("Arthena", message.OwnerName);
        Assert.Equal("Kronos", message.MemberName);
        Assert.Equal(3, message.SeatIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("""{ "owner": "", "member": "Kronos", "seat": 3 }""")]
    [InlineData("""{ "owner": "Arthena", "member": "Kronos", "seat": 0 }""")] // passenger seats are 1-based
    public void PillionAssignment_BadInput_ReturnsNull(string? json)
    {
        Assert.Null(CharonPillionIpc.Parse(json));
    }

    // --- Graceful fallback when Daedalus is not loaded ---

    private sealed class UnavailableRoster : IDaedalusRosterProvider
    {
        public bool IsAvailable => false;
        public IReadOnlyList<LanToonInfo> GetLanPartyMembers() => Array.Empty<LanToonInfo>();
        public IReadOnlyList<string> GetTrustList() => Array.Empty<string>();
    }

    [Fact]
    public void DaedalusUnavailable_ManualWhitelistStillAccepts()
    {
        var entries = new List<WhitelistEntry>
        {
            new() { CharacterName = "OldFriend", World = "Gilgamesh", Enabled = true },
        };
        var whitelist = new WhitelistService(entries, () => { });
        var manager = new GroupInviteManager(
            new AutoAcceptConfig(Enabled: true, LanAutoWhitelist: true),
            whitelist,
            new UnavailableRoster(),
            () => { },
            new Random(1));

        Assert.Equal(InviteDecision.AcceptScheduled,
            manager.OnInviteReceived("OldFriend", "Gilgamesh", T0));
    }

    [Fact]
    public void DaedalusUnavailable_LanAutoWhitelistTrustsNobody()
    {
        var whitelist = new WhitelistService(new List<WhitelistEntry>(), () => { });
        var manager = new GroupInviteManager(
            new AutoAcceptConfig(Enabled: true, LanAutoWhitelist: true),
            whitelist,
            new UnavailableRoster(),
            () => { },
            new Random(1));

        Assert.Equal(InviteDecision.Ignored, manager.OnInviteReceived("Kronos", "Ultros", T0));
    }

    [Fact]
    public void DaedalusUnavailable_ImportFromLanAddsNothing()
    {
        var whitelist = new WhitelistService(new List<WhitelistEntry>(), () => { });
        Assert.Equal(0, whitelist.ImportFromLan(new UnavailableRoster().GetLanPartyMembers()));
    }
}
