using Charon.Features.Automation;

namespace Charon.Tests.Features.Automation;

public sealed class AutomationRunsTests
{
    /// <summary>A run shaped like the real ones: it holds its own stop request and clears it when it ends.</summary>
    private sealed class FakeRun
    {
        public bool Running { get; private set; }
        public bool StopRequested { get; private set; }
        public int StopCalls { get; private set; }

        public void Start()
        {
            Running = true;
            StopRequested = false;
        }

        public void StopAfterStep()
        {
            StopCalls++;
            if (Running)
                StopRequested = true;
        }

        /// <summary>The step in hand ends: a requested stop lands here.</summary>
        public void FinishStep()
        {
            if (!StopRequested)
                return;

            Running = false;
            StopRequested = false;
        }

        public AutomationRun AsRun(string name) => new(name, () => Running, StopAfterStep);
    }

    [Fact]
    public void Idle_RequestReturnsFalse_AndTouchesNothing()
    {
        var a = new FakeRun();
        var b = new FakeRun();
        var runs = new AutomationRuns([a.AsRun("a"), b.AsRun("b")]);

        Assert.False(runs.RequestStopAfterRun());
        Assert.False(runs.IsBusy);
        Assert.Equal(0, a.StopCalls);
        Assert.Equal(0, b.StopCalls);
    }

    [Fact]
    public void OnlyTheBusyRunsAreAskedToStop()
    {
        var idle = new FakeRun();
        var busy = new FakeRun();
        busy.Start();
        var runs = new AutomationRuns([idle.AsRun("idle"), busy.AsRun("busy")]);

        Assert.True(runs.IsBusy);
        Assert.Equal(new[] { "busy" }, runs.BusyNames());
        Assert.True(runs.RequestStopAfterRun());
        Assert.Equal(0, idle.StopCalls);
        Assert.True(busy.StopRequested);
    }

    [Fact]
    public void StaysBusyUntilTheStepEnds_AndASecondRequestIsNotAToggle()
    {
        var run = new FakeRun();
        run.Start();
        var runs = new AutomationRuns([run.AsRun("chest")]);

        Assert.True(runs.RequestStopAfterRun());
        Assert.True(runs.IsBusy); // finishing after a stop request still counts

        Assert.True(runs.RequestStopAfterRun()); // already stopping: still true, still stopping
        Assert.True(run.StopRequested);

        run.FinishStep();
        Assert.False(runs.IsBusy);
        Assert.False(runs.RequestStopAfterRun());
    }

    [Fact]
    public void AFreshStartAfterTheStop_IsNotStoppedByTheOldRequest()
    {
        var run = new FakeRun();
        run.Start();
        var runs = new AutomationRuns([run.AsRun("fetch")]);
        runs.RequestStopAfterRun();
        run.FinishStep();

        run.Start(); // e.g. Argus's own RequestFetch right after
        run.FinishStep();

        Assert.True(run.Running);
        Assert.True(runs.IsBusy);
        Assert.Equal(1, run.StopCalls);
    }

    [Fact]
    public void LetItFinish_LeavesASingleStepRunToEndOnItsOwn()
    {
        var busy = true;
        var runs = new AutomationRuns([new AutomationRun("gear pass", () => busy, AutomationRun.LetItFinish)]);

        Assert.True(runs.RequestStopAfterRun());
        Assert.True(runs.IsBusy);

        busy = false; // the pass ends by itself
        Assert.False(runs.IsBusy);
    }
}
