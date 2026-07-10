using Charon.Features.AutoAccept;
using Charon.Services;

namespace Charon.Tests.Features.AutoAccept;

public sealed class GroupInviteManagerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeRoster : IDaedalusRosterProvider
    {
        public bool IsAvailable { get; set; }
        public List<LanToonInfo> Toons { get; } = new();
        public IReadOnlyList<LanToonInfo> GetLanPartyMembers() => Toons;
        public IReadOnlyList<string> GetTrustList() => Array.Empty<string>();
    }

    private sealed class Harness
    {
        public FakeRoster Roster { get; } = new();
        public List<WhitelistEntry> Entries { get; } = new();
        public WhitelistService Whitelist { get; }
        public GroupInviteManager Manager { get; }
        public int Accepts { get; private set; }

        public Harness(bool enabled = true, bool lanAutoWhitelist = true, int seed = 1234)
        {
            Whitelist = new WhitelistService(Entries, () => { });
            Manager = new GroupInviteManager(
                new AutoAcceptConfig(enabled, lanAutoWhitelist),
                Whitelist,
                Roster,
                () => Accepts++,
                new Random(seed));
        }

        public void AddWhitelisted(string name, string world, bool entryEnabled = true)
        {
            Entries.Add(new WhitelistEntry { CharacterName = name, World = world, Enabled = entryEnabled });
        }

        public void AddLanToon(string name, string world = "Ultros")
        {
            Roster.Toons.Add(new LanToonInfo { CharacterName = name, World = world, IsOnline = true });
        }
    }

    // --- Whitelist matching ---

    [Fact]
    public void ManualWhitelistMatch_SchedulesAccept()
    {
        var h = new Harness();
        h.AddWhitelisted("Arthena", "Ultros");

        var decision = h.Manager.OnInviteReceived("Arthena", "Ultros", T0);

        Assert.Equal(InviteDecision.AcceptScheduled, decision);
    }

    [Fact]
    public void WhitelistMatch_IsCaseInsensitive_OnNameAndWorld()
    {
        var h = new Harness();
        h.AddWhitelisted("Arthena", "Ultros");

        Assert.Equal(InviteDecision.AcceptScheduled, h.Manager.OnInviteReceived("ARTHENA", "ultros", T0));
    }

    [Fact]
    public void WorldMismatch_IsIgnored()
    {
        var h = new Harness();
        h.AddWhitelisted("Arthena", "Ultros");

        Assert.Equal(InviteDecision.Ignored, h.Manager.OnInviteReceived("Arthena", "Gilgamesh", T0));
    }

    [Fact]
    public void DisabledWhitelistEntry_IsIgnored()
    {
        var h = new Harness();
        h.AddWhitelisted("Arthena", "Ultros", entryEnabled: false);

        Assert.Equal(InviteDecision.Ignored, h.Manager.OnInviteReceived("Arthena", "Ultros", T0));
    }

    [Fact]
    public void Stranger_IsIgnored_NeverDeclined()
    {
        var h = new Harness();

        var decision = h.Manager.OnInviteReceived("Random Stranger", "Ultros", T0);

        Assert.Equal(InviteDecision.Ignored, decision);
        h.Manager.Update(T0.AddSeconds(5));
        Assert.Equal(0, h.Accepts); // nothing fires later either
    }

    // --- LAN auto-whitelist ---

    [Fact]
    public void LanMember_AcceptedWhenLanAutoWhitelistOn()
    {
        var h = new Harness(lanAutoWhitelist: true);
        h.AddLanToon("Kronos", "Ultros");

        Assert.Equal(InviteDecision.AcceptScheduled, h.Manager.OnInviteReceived("Kronos", "Ultros", T0));
    }

    [Fact]
    public void LanMember_IgnoredWhenLanAutoWhitelistOff()
    {
        var h = new Harness(lanAutoWhitelist: false);
        h.AddLanToon("Kronos", "Ultros");

        Assert.Equal(InviteDecision.Ignored, h.Manager.OnInviteReceived("Kronos", "Ultros", T0));
    }

    [Fact]
    public void LanMember_WorldMustMatchWhenRosterCarriesOne()
    {
        var h = new Harness();
        h.AddLanToon("Kronos", "Ultros");

        Assert.Equal(InviteDecision.Ignored, h.Manager.OnInviteReceived("Kronos", "Gilgamesh", T0));
    }

    [Fact]
    public void LanMember_MatchesOnNameAlone_WhenRosterHasNoWorld()
    {
        var h = new Harness();
        h.AddLanToon("Kronos", world: "");

        Assert.Equal(InviteDecision.AcceptScheduled, h.Manager.OnInviteReceived("Kronos", "Anywhere", T0));
    }

    // --- Feature toggle ---

    [Fact]
    public void FeatureDisabled_ReturnsDisabled_EvenForWhitelisted()
    {
        var h = new Harness(enabled: false);
        h.AddWhitelisted("Arthena", "Ultros");

        Assert.Equal(InviteDecision.Disabled, h.Manager.OnInviteReceived("Arthena", "Ultros", T0));
    }

    // --- Accept delay ---

    [Fact]
    public void Accept_FiresOnlyAfterRandomDelay_WithinConfiguredWindow()
    {
        var h = new Harness();
        h.AddWhitelisted("Arthena", "Ultros");
        h.Manager.OnInviteReceived("Arthena", "Ultros", T0);

        // Must never fire before the minimum delay.
        h.Manager.Update(T0.AddSeconds(AutoAcceptConfig.MinAcceptDelaySeconds - 0.01));
        Assert.Equal(0, h.Accepts);

        // Must always have fired by the maximum delay.
        h.Manager.Update(T0.AddSeconds(AutoAcceptConfig.MaxAcceptDelaySeconds + 0.01));
        Assert.Equal(1, h.Accepts);
    }

    [Fact]
    public void Accept_DelayVaries_AcrossSeeds()
    {
        // The delay is randomized per invite — two different RNG seeds should not produce
        // the same firing pattern at a probe point inside the window.
        var results = new List<bool>();
        foreach (var seed in new[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            var h = new Harness(seed: seed);
            h.AddWhitelisted("Arthena", "Ultros");
            h.Manager.OnInviteReceived("Arthena", "Ultros", T0);
            h.Manager.Update(T0.AddSeconds(0.55)); // middle of the 0.3–0.8 window
            results.Add(h.Accepts == 1);
        }

        Assert.Contains(true, results);
        Assert.Contains(false, results);
    }

    [Fact]
    public void InviteWithdrawn_CancelsPendingAccept()
    {
        var h = new Harness();
        h.AddWhitelisted("Arthena", "Ultros");
        h.Manager.OnInviteReceived("Arthena", "Ultros", T0);

        h.Manager.OnInviteWithdrawn();
        h.Manager.Update(T0.AddSeconds(2));

        Assert.Equal(0, h.Accepts);
    }

    [Fact]
    public void AcceptedInvite_IsLogged_WithSource()
    {
        var h = new Harness();
        h.AddWhitelisted("Arthena", "Ultros");
        h.Manager.OnInviteReceived("Arthena", "Ultros", T0);
        h.Manager.Update(T0.AddSeconds(1));

        var entry = Assert.Single(h.Manager.AcceptLog);
        Assert.Equal("Arthena", entry.CharacterName);
        Assert.Equal("Ultros", entry.World);
        Assert.Equal("Manual", entry.Source);
    }

    [Fact]
    public void LanAccept_LogsLanSource()
    {
        var h = new Harness();
        h.AddLanToon("Kronos", "Ultros");
        h.Manager.OnInviteReceived("Kronos", "Ultros", T0);
        h.Manager.Update(T0.AddSeconds(1));

        Assert.Equal("LAN", Assert.Single(h.Manager.AcceptLog).Source);
    }

    // --- Repeated / replaced invites ---

    [Fact]
    public void SecondInvite_WhileFirstPending_ReplacesPendingAccept()
    {
        var h = new Harness();
        h.AddWhitelisted("Arthena", "Ultros");
        h.AddWhitelisted("Kronos", "Ultros");

        h.Manager.OnInviteReceived("Arthena", "Ultros", T0);
        h.Manager.OnInviteReceived("Kronos", "Ultros", T0.AddSeconds(0.1));
        h.Manager.Update(T0.AddSeconds(2));

        // Only one accept fires, and the log shows the latest inviter.
        Assert.Equal(1, h.Accepts);
        Assert.Equal("Kronos", Assert.Single(h.Manager.AcceptLog).CharacterName);
    }

    // --- WhitelistService behaviors surfaced through the manager's dependencies ---

    [Fact]
    public void WhitelistAdd_DeduplicatesByNameAndWorld()
    {
        var h = new Harness();
        Assert.True(h.Whitelist.Add("Arthena", "Ultros"));
        Assert.False(h.Whitelist.Add("arthena", "ULTROS")); // duplicate, case-insensitive
        Assert.Single(h.Whitelist.Entries);
    }

    [Fact]
    public void WhitelistImportFromLan_SkipsExisting()
    {
        var h = new Harness();
        h.AddLanToon("Arthena", "Ultros");
        h.AddLanToon("Kronos", "Ultros");
        h.Whitelist.Add("Arthena", "Ultros");

        var added = h.Whitelist.ImportFromLan(h.Roster.Toons);

        Assert.Equal(1, added);
        Assert.Equal(2, h.Whitelist.Entries.Count);
    }

    [Fact]
    public void WhitelistSetEnabled_TogglesWithoutRemoving()
    {
        var h = new Harness();
        h.Whitelist.Add("Arthena", "Ultros");

        Assert.True(h.Whitelist.SetEnabled("Arthena", "Ultros", false));
        Assert.False(h.Whitelist.IsWhitelisted("Arthena", "Ultros"));
        Assert.Single(h.Whitelist.Entries);

        Assert.True(h.Whitelist.SetEnabled("Arthena", "Ultros", true));
        Assert.True(h.Whitelist.IsWhitelisted("Arthena", "Ultros"));
    }
}
