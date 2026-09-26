using Charon.Features.Dailies;

namespace Charon.Tests.Features.Dailies;

/// <summary>
/// Reading the game's Timers window by LABEL. The coordinates here are the real shape of that window: a
/// label on the left, the value on the right of the same row.
/// </summary>
public class AllowancesTests
{
    private static readonly (float X, float Y, string Text)[] Window =
    [
        (30f, 100f, "Next Mission Allowance"),
        (420f, 100f, "15:15 Remaining (9/26 15:00)"),
        (30f, 124f, "Custom Deliveries"),
        (420f, 124f, "75:15 Remaining (9/29 3:00)"),
        (480f, 124f, "Allowances: 12"),
        (30f, 148f, "Adventurer Squadron"),
        (420f, 148f, "None"),
        (30f, 196f, "Ventures"),
        (420f, 196f, "0 of 2 Complete"),
        (30f, 220f, "Next Map Allowance"),
        (420f, 220f, "Available Now"),
        (30f, 244f, "Next Allied Society Daily Quest Allowance"),
        (420f, 244f, "Today's Remaining Allowances: 12"),
    ];

    /// <summary>
    /// The rows the reader actually sees come out of a list component, which reports component-relative
    /// positions — so the same visual line can sit a few pixels apart from its label. The label must still pick
    /// up its value, and the wider net must not reach into the row below.
    /// </summary>
    [Fact]
    public void AValueAFewPixelsOffItsLabelIsStillThatLabelsValue()
    {
        (float X, float Y, string Text)[] window =
        [
            (30f, 100f, "Next Mission Allowance"),
            (420f, 110f, "14:08 Remaining (9/26 15:00)"),
            (480f, 110f, "Allowances: 12"),
            (30f, 144f, "Custom Deliveries"),
            (420f, 144f, "74:08 Remaining (9/29 3:00)"),
        ];

        var line = Allowances.Find(window, "Next Mission Allowance");

        Assert.Equal(AllowanceState.Countdown, line!.State);
        Assert.Equal(14, line.Hours);
        Assert.Equal(8, line.Minutes);
    }

    /// <summary>An empty value is a state the UI can word honestly, not a crash and not a guess.</summary>
    [Fact]
    public void ALabelWithNothingBesideItReadsAsUnknown()
    {
        (float X, float Y, string Text)[] window =
        [
            (30f, 100f, "Next Mission Allowance"),
            (30f, 144f, "Custom Deliveries"),
            (420f, 144f, "74:08 Remaining (9/29 3:00)"),
        ];

        var line = Allowances.Find(window, "Next Mission Allowance");

        Assert.Equal(AllowanceState.Unknown, line!.State);
        Assert.Equal(string.Empty, line.Value);
        Assert.Equal("not read", line.Describe());
    }

    [Fact]
    public void FindsTheMissionAllowanceAndItsCountdown()
    {
        var line = Allowances.Find(Window, "Next Mission Allowance");

        Assert.NotNull(line);
        Assert.Equal(AllowanceState.Countdown, line!.State);
        Assert.Equal(15, line.Hours);
        Assert.Equal(15, line.Minutes);
        Assert.Equal("15h 15m remaining", line.Describe());
    }

    [Fact]
    public void AWindowSayingAvailableNowIsAvailable()
    {
        var line = Allowances.Find(Window, "Next Map Allowance");

        Assert.Equal(AllowanceState.Available, line!.State);
        Assert.True(line.Available);
        Assert.Equal("available now", line.Describe());
    }

    [Fact]
    public void NoneIsNoneRatherThanACountdown()
    {
        var line = Allowances.Find(Window, "Adventurer Squadron");

        Assert.Equal(AllowanceState.None, line!.State);
        Assert.Equal("none", line.Describe());
    }

    [Fact]
    public void SeventyFiveHoursIsNotClampedToADay()
    {
        // "75:15" is 75 hours in this window; a date parse would silently turn it into 3h 15m.
        var line = Allowances.Find(Window, "Custom Deliveries");

        Assert.Equal(75, line!.Hours);
        Assert.Equal(15, line.Minutes);
    }

    [Fact]
    public void TheValueIsTakenFromTheRightOfItsOwnRow()
    {
        // Rows with a second value column ("Allowances: 12") must not become the answer for a neighbouring row.
        var line = Allowances.Find(Window, "Ventures");

        Assert.Equal("0 of 2 Complete", line!.Value);
        Assert.Equal(AllowanceState.Unknown, line.State); // not a wording we claim to understand
    }

    [Fact]
    public void ALabelWithNoValueOnItsRow_TakesTheNextRowWhenThatRowIsNotAnotherLabel()
    {
        var nodes = new (float X, float Y, string Text)[]
        {
            (30f, 100f, "Next Mission Allowance"),
            (30f, 118f, "15:15 Remaining (9/26 15:00)"),
        };

        var line = Allowances.Find(nodes, "Next Mission Allowance");

        Assert.Equal(15, line!.Hours);
    }

    [Fact]
    public void ALabelAndValueInOneString_AreSplit()
    {
        var nodes = new (float X, float Y, string Text)[]
        {
            (30f, 100f, "Next Map Allowance: Available Now"),
        };

        var line = Allowances.Find(nodes, "Next Map Allowance");

        Assert.Equal("Next Map Allowance", line!.Label);
        Assert.Equal("Available Now", line.Value);
        Assert.True(line.Available);
    }

    [Fact]
    public void AMissingLabel_ReturnsNullRatherThanTheWrongRow()
    {
        Assert.Null(Allowances.Find(Window, "Next Wonderous Tails Book"));
    }

    [Theory]
    [InlineData("Available Now", AllowanceState.Available)]
    [InlineData("75:15 Remaining (9/29 3:00)", AllowanceState.Countdown)]
    [InlineData("None", AllowanceState.None)]
    [InlineData("75:15 Remaining (9/29 3:00) Incomplete", AllowanceState.Countdown)]
    [InlineData("0 of 2 Complete", AllowanceState.Unknown)]
    [InlineData("", AllowanceState.Unknown)]
    public void TheGamesWording_mapsToAState(string value, AllowanceState expected)
    {
        Assert.Equal(expected, Allowances.Parse(value));
    }

    [Fact]
    public void RowsAreGroupedWithAToleranceSoTinyYJitterDoesNotSplitALine()
    {
        var nodes = new (float X, float Y, string Text)[]
        {
            (30f, 100f, "Next Mission Allowance"),
            (420f, 102f, "15:15 Remaining (9/26 15:00)"),
        };

        Assert.Equal(15, Allowances.Find(nodes, "Next Mission Allowance")!.Hours);
    }
}
