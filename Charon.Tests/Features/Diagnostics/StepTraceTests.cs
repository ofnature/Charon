using Charon.Features.Diagnostics;

namespace Charon.Tests.Features.Diagnostics;

public sealed class StepTraceTests
{
    private static readonly DateTime At = new(2026, 9, 27, 0, 21, 22, 500);

    [Fact]
    public void NewestIsFirst()
    {
        var trace = new StepTrace();
        trace.Record(At, "Select(T'sala)");
        trace.Record(At.AddSeconds(1), "OpenItems(T'sala)");

        Assert.EndsWith("OpenItems(T'sala)", trace.Lines[0]);
        Assert.EndsWith("Select(T'sala)", trace.Lines[1]);
    }

    [Fact]
    public void LinesCarryTheirTime()
    {
        var trace = new StepTrace();
        trace.Record(At, "Select(T'sala)");
        Assert.Equal("00:21:22.5  Select(T'sala)", trace.Lines[0]);
    }

    [Fact]
    public void ItIsBounded_DroppingTheOldest()
    {
        var trace = new StepTrace(capacity: 3);
        for (var i = 0; i < 5; i++)
            trace.Record(At.AddSeconds(i), $"step {i}");

        Assert.Equal(3, trace.Lines.Count);
        Assert.EndsWith("step 4", trace.Lines[0]);
        Assert.EndsWith("step 2", trace.Lines[2]);
    }

    [Fact]
    public void AStallIsNotedOnce_NotEveryFrame()
    {
        // Unfiltered, a wait would flood the buffer; actions-only, a hang is invisible.
        var trace = new StepTrace();
        Assert.True(trace.Note(At, "waiting for a bell", "(waiting for a bell)"));
        Assert.False(trace.Note(At.AddSeconds(1), "waiting for a bell", "(waiting for a bell)"));
        Assert.Single(trace.Lines);
    }

    [Fact]
    public void ANewReasonIsNoted()
    {
        var trace = new StepTrace();
        trace.Note(At, "a", "(a)");
        Assert.True(trace.Note(At, "b", "(b)"));
        Assert.Equal(2, trace.Lines.Count);
    }

    [Fact]
    public void AnActionReArmsTheSameReason()
    {
        // After doing something, the same wait is news again.
        var trace = new StepTrace();
        trace.Note(At, "waiting", "(waiting)");
        trace.Record(At.AddSeconds(1), "Select(T'sola)");
        Assert.True(trace.Note(At.AddSeconds(2), "waiting", "(waiting)"));
    }

    [Fact]
    public void TextCarriesExtraEvidenceUnderItsOwnHeading()
    {
        var trace = new StepTrace();
        trace.Record(At, "Select(T'sala)");

        var text = trace.ToText([("RetainerList values", ["[0] 2", "[3] \"T'sala\""])]);

        Assert.Contains("Select(T'sala)", text);
        Assert.Contains("--- RetainerList values ---", text);
        Assert.Contains("[3] \"T'sala\"", text);
    }

    [Fact]
    public void EmptyExtrasAddNoHeading()
    {
        var trace = new StepTrace();
        trace.Record(At, "x");
        Assert.DoesNotContain("---", trace.ToText([("Nothing", [])]));
    }

    [Fact]
    public void ClearForgetsTheLastNote()
    {
        var trace = new StepTrace();
        trace.Note(At, "waiting", "(waiting)");
        trace.Clear();
        Assert.Empty(trace.Lines);
        Assert.True(trace.Note(At, "waiting", "(waiting)"));
    }
}
