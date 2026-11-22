using Charon.Features.AutoLogin;
using Xunit;

namespace Charon.Tests.Features.AutoLogin;

public class LobbyNoticeTests
{
    // Lobby sheet row 1237, as read from the game files (the sheet has one line break).
    private const string Sheet =
        "The character you last logged out with in this play environment could not be found on the current data center.\n"
        + "Please connect to another data center from the Data Center Selection screen.";

    [Fact]
    public void TheSheetTextMatchesItself()
    {
        Assert.True(LobbyNotice.Same(Sheet, Sheet));
    }

    [Fact]
    public void TheWindowsOwnLineBreaks_DoNotMatter()
    {
        // As wrapped on screen (user screenshot, 2026-11-21).
        const string shown =
            "The character you last logged out with in this\r\n"
            + "play environment could not be found on the\r\n"
            + "current data center.\r\n"
            + "Please connect to another data center from the\r\n"
            + "Data Center Selection screen.";

        Assert.True(LobbyNotice.Same(shown, Sheet));
    }

    [Fact]
    public void ALineBreakDroppedAltogether_StillMatches()
    {
        Assert.True(LobbyNotice.Same(Sheet.Replace("\n", string.Empty), Sheet));
    }

    [Fact]
    public void AnotherBoxsText_DoesNotMatch()
    {
        Assert.False(LobbyNotice.Same("This World is currently full. Players in queue: 12", Sheet));
        Assert.False(LobbyNotice.Same(Sheet + " Cancel", Sheet));
    }

    [Theory]
    [InlineData(null, Sheet)]
    [InlineData("", Sheet)]
    [InlineData("   ", Sheet)]
    [InlineData(Sheet, null)]
    [InlineData(Sheet, "")]
    [InlineData("", "")]
    public void EmptyOnEitherSide_NeverMatches(string? shown, string? sheet)
    {
        Assert.False(LobbyNotice.Same(shown, sheet));
    }
}
