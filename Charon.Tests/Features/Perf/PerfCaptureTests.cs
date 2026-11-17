using System;
using System.Linq;
using Charon.Features.Perf;

namespace Charon.Tests.Features.Perf;

public sealed class PerfCaptureTests
{
    private static PerfSample Sample(string step, int frames = 60, double totalMs = 1000, double worst = 20,
        double cpu = 2, double? gpu = 10, double? video = 2000, bool counted = true) =>
        new(new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc), step, counted, false, false, 132,
            frames, totalMs, worst, cpu, 3000, 8000, gpu, video, "");

    [Fact]
    public void Csv_HasOneValuePerHeaderColumn_AndQuotesText()
    {
        var line = PerfCsv.Line(Sample("cap, 50%") with { Applied = "frame cap on; 3D resolution 50%" });
        Assert.Contains("\"cap, 50%\"", line);
        Assert.Contains(",60.0,", line);
        Assert.StartsWith("2026-11-15T12:00:00.000Z,", line);
        // Two quoted fields each hold a comma or none; strip them before counting columns.
        var bare = line.Replace("\"cap, 50%\"", "x").Replace("\"frame cap on; 3D resolution 50%\"", "y");
        Assert.Equal(PerfCsv.Header.Split(',').Length, bare.Split(',').Length);
    }

    [Fact]
    public void Csv_LeavesUnreadGpuFiguresBlank()
    {
        var line = PerfCsv.Line(Sample("x", gpu: null, video: null));
        Assert.Contains(",,,", line);
    }

    [Fact]
    public void Summary_GroupsByStepInOrder_AndSkipsUncountedSeconds()
    {
        var rows = PerfSummary.ByStep(
        [
            Sample("your settings"),
            Sample("your settings", counted: false, cpu: 99),
            Sample("15 fps", frames: 15, cpu: 1, gpu: 4),
            Sample("your settings"),
        ]);

        Assert.Equal(new[] { "your settings", "15 fps" }, rows.Select(r => r.Step));
        Assert.Equal(2, rows[0].Seconds);
        Assert.Equal(2.0, rows[0].CpuCores, 3);
        Assert.Equal(15.0, rows[1].Fps, 3);
    }

    [Fact]
    public void FrameRate_IsFramesOverTime_NotAMeanOfRates()
    {
        // A second at 100 fps and a 4-second stall of 4 frames: 104 frames in 5s = 20.8 fps, not ~50.
        var rows = PerfSummary.ByStep([Sample("x", frames: 100, totalMs: 1000), Sample("x", frames: 4, totalMs: 4000)]);
        Assert.Equal(20.8, rows[0].Fps, 3);
    }

    [Fact]
    public void WorstFrame_IsTheWorstOfTheStep()
    {
        var rows = PerfSummary.ByStep([Sample("x", worst: 20), Sample("x", worst: 180)]);
        Assert.Equal(180, rows[0].WorstMs);
    }

    [Fact]
    public void Change_IsAgainstTheFirstRow()
    {
        Assert.Equal("-50%", PerfSummary.Change(1, 2, false));
        Assert.Equal("+25%", PerfSummary.Change(2.5, 2, false));
        Assert.Equal("", PerfSummary.Change(1, 2, true));
        Assert.Equal("", PerfSummary.Change(null, 2, false));
        Assert.Equal("", PerfSummary.Change(1, 0, false));
    }

    [Fact]
    public void Table_StaysAlignedWithALongStepName()
    {
        var table = PerfSummary.Table(PerfSummary.ByStep(
            [Sample("your settings"), Sample("ShadowCascadeCountType_DX11 = 0 (yours 1)")]));
        var lines = table.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal(lines[1].Length, lines[2].Length);
    }

    [Fact]
    public void Table_ShowsEachStepWithItsSaving()
    {
        var table = PerfSummary.Table(PerfSummary.ByStep(
            [Sample("your settings", cpu: 2, gpu: 10), Sample("15 fps", frames: 15, cpu: 1, gpu: 4)]));
        var lines = table.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("1.00 (-50%)", lines[2]);
        Assert.Contains("4.0 (-60%)", lines[2]);
        Assert.Equal("nothing counted yet", PerfSummary.Table([]));
    }

    [Fact]
    public void Sweep_SettlesThenCountsThenMovesOn()
    {
        var sweep = new PerfSweepState();
        for (var i = 0; i < PerfSweepPlan.SettleSeconds; i++)
            Assert.False(sweep.Tick(true));
        for (var i = 0; i < PerfSweepPlan.HoldSeconds; i++)
        {
            Assert.Equal(0, sweep.Index);
            Assert.True(sweep.Tick(true));
        }

        Assert.Equal(1, sweep.Index);
        Assert.True(sweep.Settling);
    }

    [Fact]
    public void Sweep_AnInterruption_StartsTheSettleAgain_ButKeepsWhatWasCounted()
    {
        var sweep = new PerfSweepState();
        for (var i = 0; i < PerfSweepPlan.SettleSeconds + 4; i++)
            sweep.Tick(true);
        Assert.Equal(4, sweep.Counted);

        Assert.False(sweep.Tick(false)); // focused, in combat or loading
        for (var i = 0; i < PerfSweepPlan.SettleSeconds; i++)
            Assert.False(sweep.Tick(true));
        Assert.True(sweep.Tick(true));
        Assert.Equal(5, sweep.Counted);
    }

    [Fact]
    public void Sweep_Finishes_AfterEveryStep()
    {
        var sweep = new PerfSweepState();
        var counted = 0;
        for (var i = 0; i < 10_000 && !sweep.Done; i++)
            if (sweep.Tick(true))
                counted++;

        Assert.True(sweep.Done);
        Assert.Null(sweep.Current);
        Assert.Equal(PerfSweepPlan.Steps.Count * PerfSweepPlan.HoldSeconds, counted);
        Assert.False(sweep.Tick(true));
    }

    [Fact]
    public void MemoryProbe_TriesEveryOtherValue_BetweenTwoBaselines()
    {
        OptionRange? Read(string option) => option switch
        {
            "TextureRezoType" => new OptionRange(0, 1, 1),
            "ShadowTextureSizeType_DX11" => new OptionRange(0, 2, 1),
            "LodType_DX11" => new OptionRange(0, 1000, 1), // not a quality level: skipped
            _ => null,                                     // unreadable: skipped
        };

        var steps = PerfSweepPlan.MemorySteps(Read, 1920, 1009);
        Assert.Equal(
        [
            "your settings",
            "fewest characters",
            "TextureRezoType = 0 (yours 1)",
            "ShadowTextureSizeType_DX11 = 0 (yours 1)",
            "ShadowTextureSizeType_DX11 = 2 (yours 1)",
            "window 1280x720 (yours 1920x1009)",
            "window 960x540 (yours 1920x1009)",
            "your settings (end)",
        ], steps.Select(s => s.Name));
        Assert.True(steps[1].Choice!.FewerCharacters);
        Assert.Equal(("TextureRezoType", 0u), (steps[2].Option, steps[2].Value));
        Assert.Equal((960, 540), (steps[6].Width, steps[6].Height));
    }

    [Fact]
    public void MemoryProbe_NeverGrowsTheWindow()
    {
        var steps = PerfSweepPlan.MemorySteps(_ => null, 1024, 600);
        Assert.Equal(
            new[] { "your settings", "fewest characters", "window 960x540 (yours 1024x600)", "your settings (end)" },
            steps.Select(s => s.Name));
    }

    [Fact]
    public void Sweep_HonoursItsOwnSettleAndHold()
    {
        var sweep = new PerfSweepState(PerfSweepPlan.Steps, settleSeconds: 2, holdSeconds: 3);
        Assert.False(sweep.Tick(true));
        Assert.False(sweep.Tick(true));
        Assert.True(sweep.Tick(true));
        Assert.True(sweep.Tick(true));
        Assert.Equal(0, sweep.Index);
        Assert.True(sweep.Tick(true));
        Assert.Equal(1, sweep.Index);
    }

    [Fact]
    public void ThePlan_StartsWithTheBaseline_AndEveryStepDoesOneNamedThing()
    {
        Assert.Null(PerfSweepPlan.Steps[0].Choice);
        Assert.Equal(0, PerfSweepPlan.Steps[0].TargetFps);
        Assert.Equal(PerfSweepPlan.Steps.Count, PerfSweepPlan.Steps.Select(s => s.Name).Distinct().Count());
        // A paced step leaves the game's own settings alone, so the two are never measured together.
        Assert.All(PerfSweepPlan.Steps.Where(s => s.TargetFps > 0), s => Assert.Null(s.Choice));
        // Every profile step lets a fight have normal rendering: combat pauses the sweep anyway.
        Assert.All(PerfSweepPlan.Steps.Where(s => s.Choice != null), s => Assert.True(s.Choice!.NormalInCombat));
    }
}
