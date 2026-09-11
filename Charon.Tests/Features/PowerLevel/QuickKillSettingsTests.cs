namespace Charon.Tests.Features.PowerLevel;

public sealed class QuickKillSettingsTests
{
    [Fact]
    public void UnknownCharacter_IsOff()
    {
        var setting = new CharonConfig().QuickKillFor(42);
        Assert.False(setting.Enabled);
        Assert.Equal(0, setting.Mode);
    }

    [Fact]
    public void EachCharacterKeepsItsOwnRole()
    {
        // Every client on a PC shares one Charon.json — a carry and a hand-played toon on the same
        // machine must still be able to hold different roles.
        var config = new CharonConfig();
        config.QuickKillFor(1).Enabled = true;
        config.QuickKillFor(2).Mode = 1;

        Assert.True(config.QuickKillFor(1).Enabled);
        Assert.Equal(0, config.QuickKillFor(1).Mode);
        Assert.False(config.QuickKillFor(2).Enabled);
        Assert.Equal(1, config.QuickKillFor(2).Mode);
    }

    [Fact]
    public void NotLoggedIn_GetsAThrowawayEntry()
    {
        var config = new CharonConfig();
        config.QuickKillFor(0).Enabled = true;

        Assert.Empty(config.QuickKillByCharacter);
        Assert.False(config.QuickKillFor(0).Enabled);
    }
}
