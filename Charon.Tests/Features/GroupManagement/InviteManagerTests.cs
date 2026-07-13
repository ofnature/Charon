using Charon.Features.GroupManagement;
using Charon.Services;

namespace Charon.Tests.Features.GroupManagement;

public sealed class InviteManagerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static LanToonInfo Toon(string name, bool online = true) =>
        new() { CharacterName = name, World = "Ultros", IsOnline = online, ContentId = 1, WorldId = 405 };

    private sealed class Harness
    {
        public List<string> Sent { get; } = new();
        public InviteManager Manager { get; }
        public HashSet<string> InParty { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Harness(int seed = 7)
        {
            Manager = new InviteManager(
                toon => { Sent.Add(toon.CharacterName); return (true, $"invite sent to {toon.CharacterName}"); },
                new Random(seed));
        }

        public int InviteAll(int partySize, DateTime now, params LanToonInfo[] roster) =>
            Manager.InviteAll(roster, "Local Toon", partySize, n => InParty.Contains(n), now);
    }

    [Fact]
    public void MassInvite_SkipsOfflineToons()
    {
        var h = new Harness();
        var queued = h.InviteAll(1, T0, Toon("Styx"), Toon("Acheron", online: false), Toon("Lethe"));

        Assert.Equal(2, queued);
        h.Manager.Update(T0.AddSeconds(5));
        Assert.Equal(new[] { "Styx", "Lethe" }, h.Sent);
    }

    [Fact]
    public void MassInvite_SkipsToonsAlreadyInGroup()
    {
        var h = new Harness();
        h.InParty.Add("Styx");
        var queued = h.InviteAll(2, T0, Toon("Styx"), Toon("Acheron"));

        Assert.Equal(1, queued);
        h.Manager.Update(T0.AddSeconds(5));
        Assert.Equal(new[] { "Acheron" }, h.Sent);
    }

    [Fact]
    public void MassInvite_SkipsSelf()
    {
        var h = new Harness();
        var queued = h.InviteAll(1, T0, Toon("Local Toon"), Toon("Acheron"));

        Assert.Equal(1, queued);
        h.Manager.Update(T0.AddSeconds(5));
        Assert.Equal(new[] { "Acheron" }, h.Sent);
    }

    [Fact]
    public void MassInvite_RespectsEightSlotCap()
    {
        var h = new Harness();
        var roster = Enumerable.Range(0, 10).Select(i => Toon($"Toon{i}")).ToArray();
        var queued = h.InviteAll(6, T0, roster); // 6 in party → room for 2

        Assert.Equal(2, queued);
    }

    [Fact]
    public void MassInvite_SoloCountsAsPartyOfOne()
    {
        var h = new Harness();
        var roster = Enumerable.Range(0, 10).Select(i => Toon($"Toon{i}")).ToArray();
        var queued = h.InviteAll(0, T0, roster); // solo (party list length 0) → room for 7

        Assert.Equal(7, queued);
    }

    [Fact]
    public void MassInvite_StaggersSends_InConfiguredWindow()
    {
        var h = new Harness();
        h.InviteAll(1, T0, Toon("A"), Toon("B"), Toon("C"));

        // Nothing but the first at T0.
        h.Manager.Update(T0);
        Assert.Single(h.Sent);

        // Second lands no earlier than the minimum stagger…
        h.Manager.Update(T0 + InviteManager.MinStagger - TimeSpan.FromMilliseconds(10));
        Assert.Single(h.Sent);

        // …and no later than the maximum.
        h.Manager.Update(T0 + InviteManager.MaxStagger);
        Assert.Equal(2, h.Sent.Count);

        // All out by the worst-case total.
        h.Manager.Update(T0 + InviteManager.MaxStagger + InviteManager.MaxStagger);
        Assert.Equal(3, h.Sent.Count);
    }

    [Fact]
    public void InviteSingle_SendsOnNextUpdate_AndDeduplicates()
    {
        var h = new Harness();
        h.Manager.InviteSingle(Toon("Styx"), T0);
        h.Manager.InviteSingle(Toon("Styx"), T0); // duplicate while queued — ignored

        h.Manager.Update(T0);
        Assert.Equal(new[] { "Styx" }, h.Sent);
    }

    [Fact]
    public void InviteLog_RecordsResults_NewestFirst()
    {
        var h = new Harness();
        h.Manager.InviteSingle(Toon("Styx"), T0);
        h.Manager.Update(T0);
        h.Manager.InviteSingle(Toon("Acheron"), T0.AddSeconds(1));
        h.Manager.Update(T0.AddSeconds(1));

        Assert.Equal(2, h.Manager.InviteLog.Count);
        Assert.Equal("Acheron", h.Manager.InviteLog[0].Name);
        Assert.True(h.Manager.InviteLog[0].Success);
    }
}
