using System;
using System.Collections.Generic;
using Charon.Features.Retainers;
using Charon.Services.Game;
using Dalamud.Plugin.Services;
using Moq;

namespace Charon.Tests.Features.Retainers;

public sealed class VentureRoundTests
{
    private static VentureRow Row(string name, VentureState state) => new(name, state, null);

    private static readonly IReadOnlyList<VentureRow> Board =
    [
        Row("T'sala", VentureState.Ready),
        Row("T'sola", VentureState.Running),
        Row("Third", VentureState.Idle),
        Row("Fourth", VentureState.Unknown),
    ];

    [Fact]
    public void TheFirstDueRetainerIsNext()
    {
        var step = VentureRound.Next(Board, [], includeIdle: true);

        Assert.Equal(RoundKind.Serve, step.Kind);
        Assert.Equal("T'sala", step.Name);
    }

    [Fact]
    public void ARetainerStillOutIsNotOpened_NorOneWhoseTimerIsUnknown()
    {
        // After T'sala, the next due one is the idle Third — not T'sola (out) and not Fourth (unknown).
        var step = VentureRound.Next(Board, ["T'sala"], includeIdle: true);
        Assert.Equal("Third", step.Name);
    }

    [Fact]
    public void CollectAll_LeavesTheIdleOnesAlone()
    {
        var step = VentureRound.Next(Board, ["T'sala"], includeIdle: false);
        Assert.Equal(RoundKind.Done, step.Kind);
    }

    [Fact]
    public void ARetainerIsServedOncePerRound_EvenIfSheIsStillDue()
    {
        // One the game would not send stays Idle on the board; without this she would be opened forever.
        IReadOnlyList<VentureRow> stuck = [Row("Third", VentureState.Idle)];

        Assert.Equal(RoundKind.Done, VentureRound.Next(stuck, ["Third"], includeIdle: true).Kind);
    }

    [Fact]
    public void AnEmptyBoardIsNotTheEndOfTheRound()
    {
        // The roster reloads while a retainer is dismissed and reads as nobody — a transient nothing.
        Assert.Equal(RoundKind.Wait, VentureRound.Next([], ["T'sala"], includeIdle: true).Kind);
    }

    [Fact]
    public void WhenEveryoneIsDone_TheRoundEnds()
    {
        Assert.Equal(RoundKind.Done, VentureRound.Next(Board, ["T'sala", "Third"], includeIdle: true).Kind);
    }

    // --- The runner's side of a round: the intention, which is all that can be tested without a client ---

    private static VentureRunner Runner() =>
        new(Mock.Of<IGameGui>(), Mock.Of<IDataManager>(), Mock.Of<IPluginLog>());

    [Fact]
    public void ArmingARoundTargetsNobodyYet()
    {
        var runner = Runner();

        runner.ArmRound(_ => new RoundStep(RoundKind.Done));

        Assert.True(runner.Armed);
        Assert.True(runner.RoundActive);
        Assert.Null(runner.TargetName); // chosen from live state at the bell, never in advance
    }

    [Fact]
    public void StoppingARoundLeavesNothingQueued()
    {
        var runner = Runner();
        runner.ArmRound(_ => new RoundStep(RoundKind.Serve, "T'sala", 903));

        runner.Stop("stopped");

        Assert.False(runner.Armed);
        Assert.False(runner.RoundActive);
        Assert.Null(runner.TargetName);
    }

    [Fact]
    public void ARowButtonAfterARound_IsNotARound()
    {
        var runner = Runner();
        runner.ArmRound(_ => new RoundStep(RoundKind.Done));

        runner.ArmFor("T'sola", 903);

        Assert.False(runner.RoundActive);
        Assert.Equal("T'sola", runner.TargetName);
    }
}
