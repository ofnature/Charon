using System;
using System.Linq;
using Charon.Features.AutoLogin;

namespace Charon.Tests.Features.AutoLogin;

public sealed class KnownCharactersTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 3, 0, 0, DateTimeKind.Utc);

    private static KnownCharacter C(ulong id, string name, string world = "Maduin", int minutes = 0) =>
        new(id, name, world, T0.AddMinutes(minutes));

    [Fact]
    public void TwoClientsLists_BecomeOne_WithEveryCharacterOnce()
    {
        var merged = KnownCharacters.Merge(new[] { C(1, "Korha"), C(2, "Saar") }, new[] { C(2, "Saar"), C(3, "Grakur") });

        Assert.Equal(new ulong[] { 3, 1, 2 }, merged.Select(c => c.ContentId));
    }

    [Fact]
    public void TheNewestCopyOfACharacterWins_SoARenameOrTransferReplacesTheOldLabel()
    {
        var merged = KnownCharacters.Merge(
            new[] { C(1, "Korha", "Maduin", minutes: 0) },
            new[] { C(1, "Korha", "Balmung", minutes: 5) });

        var only = Assert.Single(merged);
        Assert.Equal("Balmung", only.World);
    }

    [Fact]
    public void TheOrderIsAlphabetical_WhoeverLoggedInLast()
    {
        var merged = KnownCharacters.Merge(new[] { C(1, "saar", minutes: 9), C(2, "Korha", minutes: 1) }, Array.Empty<KnownCharacter>());

        Assert.Equal(new[] { "Korha", "saar" }, merged.Select(c => c.Name));
    }

    [Fact]
    public void EntriesWithNoIdOrNoName_AreDropped()
    {
        var merged = KnownCharacters.Merge(new[] { C(0, "Nobody"), C(5, " "), C(6, "Real") }, Array.Empty<KnownCharacter>());

        Assert.Equal("Real", Assert.Single(merged).Name);
    }

    [Fact]
    public void Forgetting_RemovesOnlyThatCharacter()
    {
        var left = KnownCharacters.Without(new[] { C(1, "Korha"), C(2, "Saar") }, 1);

        Assert.Equal(2UL, Assert.Single(left).ContentId);
    }

    [Fact]
    public void AFileFromBeforeJobsWereKept_StillReads_WithNothingMissingAsNull()
    {
        var old = "[{\"ContentId\":7,\"Name\":\"Korha\",\"World\":\"Maduin\",\"SeenUtc\":\"2026-11-17T00:14:44Z\"}]";

        var only = Assert.Single(KnownCharacters.FromJson(old));

        Assert.Equal(string.Empty, only.Job);
        Assert.Equal(0, only.Level);
    }

    [Fact]
    public void JobLevelAndRole_SurviveTheFile()
    {
        var card = new KnownCharacter(7, "Korha", "Maduin", T0, "MCH", 100, 3);

        Assert.Equal(card, Assert.Single(KnownCharacters.FromJson(KnownCharacters.ToJson(new[] { card }))));
    }

    [Theory]
    [InlineData("Maduin", 100, "Maduin · Lv 100")]
    [InlineData("Maduin", 0, "Maduin")]
    [InlineData("", 21, "Lv 21")]
    [InlineData("", 0, "")]
    public void TheSubtitle_ShowsOnlyWhatIsKnown(string world, int level, string expected)
    {
        Assert.Equal(expected, KnownCharacters.Subtitle(world, level));
    }

    [Theory]
    [InlineData("Korha Ishere", "KI")]
    [InlineData("korha", "K")]
    [InlineData("A B C", "AB")]
    [InlineData("  ", "")]
    public void Initials_AreTheFirstTwoWords(string name, string expected)
    {
        Assert.Equal(expected, KnownCharacters.Initials(name));
    }

    [Theory]
    [InlineData(-5, "just now")]
    [InlineData(30, "just now")]
    [InlineData(60 * 5, "5m ago")]
    [InlineData(3600 * 3, "3h ago")]
    [InlineData(3600 * 47, "47h ago")]
    [InlineData(3600 * 49, "2d ago")]
    public void Ago_UsesOneShortUnit(int seconds, string expected)
    {
        Assert.Equal(expected, KnownCharacters.Ago(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void TheFileRoundTrips_IncludingAContentIdAboveTheSignedRange()
    {
        var big = 0xF000_0000_0000_0001UL;
        var back = KnownCharacters.FromJson(KnownCharacters.ToJson(new[] { C(big, "Korha"), C(2, "Saar") }));

        Assert.Equal(2, back.Count);
        Assert.Equal(C(big, "Korha"), back.Single(c => c.Name == "Korha"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    [InlineData("[null]")]
    public void AnUnreadableFile_IsAnEmptyList(string? json)
    {
        Assert.Empty(KnownCharacters.FromJson(json));
    }

    [Theory]
    [InlineData("Korha", "Maduin", "Korha@Maduin")]
    [InlineData("Korha", "", "Korha")]
    public void TheLabel_LeavesOutAnUnknownWorld(string name, string world, string expected)
    {
        Assert.Equal(expected, KnownCharacters.Label(name, world));
    }
}
