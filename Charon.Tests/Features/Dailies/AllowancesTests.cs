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
    /// The Timers window's rows as the client actually passes them to the addon: a label, a small int, then the
    /// value. Copied from a live dump of ContentsInfo, including the leading spaces it puts on the countdowns.
    /// </summary>
    [Fact]
    public void RowsComeOutOfTheValueArrayLabelIntValue()
    {
        (bool IsText, string Text)[] values =
        [
            (false, "258079"),
            (false, "False"),
            (false, "0"),
            (true, "Next Leve Allowance"),
            (false, "0"),
            (true, " 6:02 Remaining  (9/26 7:00)"),
            (true, "Next Mission Allowance"),
            (false, "2"),
            (true, " 14:02 Remaining  (9/26 15:00)"),
            (true, "Review the list of items being requested."),
            (true, "Next Map Allowance"),
            (false, "1"),
            (true, "Available Now"),
        ];

        var pairs = Allowances.Pairs(values);

        Assert.Contains(pairs, p => p.Label == "Next Mission Allowance"
                                    && p.Value == "14:02 Remaining  (9/26 15:00)");
        Assert.Contains(pairs, p => p.Label == "Next Leve Allowance"
                                    && p.Value == "6:02 Remaining  (9/26 7:00)");
        Assert.Contains(pairs, p => p.Label == "Next Map Allowance" && p.Value == "Available Now");
    }

    /// <summary>What those value strings parse to — the whole point of reading them.</summary>
    [Fact]
    public void AValueRowParsesLikeAnyOtherLine()
    {
        Assert.Equal(AllowanceState.Countdown, Allowances.Parse("14:02 Remaining  (9/26 15:00)"));
        Assert.Equal(14, Allowances.Hours("14:02 Remaining  (9/26 15:00)"));
        Assert.Equal(2, Allowances.Minutes("14:02 Remaining  (9/26 15:00)"));
        Assert.Equal(AllowanceState.Available, Allowances.Parse("Available Now"));
    }

    /// <summary>
    /// "Ventures" must not match the sentence "No ventures in progress." — that loose match is how a scan
    /// accepted a different window as the Timers window and reported three unrelated lines as allowances.
    /// </summary>
    [Fact]
    public void ALabelMustStartTheTextNotMerelyAppearInIt()
    {
        Assert.True(Allowances.IsLabel("Ventures", "Ventures"));
        Assert.True(Allowances.IsLabel("Next Allied Society Daily Quest Allowance", "Next Allied Society"));
        Assert.False(Allowances.IsLabel("No ventures in progress.", "Ventures"));
        Assert.False(Allowances.IsLabel("Today's Remaining Allowances: 12", "Next Leve Allowance"));
    }

    /// <summary>The Timers window shows several labels at once, and they are what identify it.</summary>
    [Fact]
    public void TheWindowsOwnLinesAreTheOnesThatIdentifyIt()
    {
        var labels = Window.Select(n => n.Text).ToList();

        Assert.True(labels.Count(t => Allowances.KnownLabels.Any(k => Allowances.IsLabel(t, k))) >= 3);
        Assert.Contains(labels, t => Allowances.Signatures.Any(s => Allowances.IsLabel(t, s)));
    }

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
