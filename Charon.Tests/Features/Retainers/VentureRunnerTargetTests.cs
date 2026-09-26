using Charon.Services.Game;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Moq;

namespace Charon.Tests.Features.Retainers;

/// <summary>
/// Aiming a run at ONE retainer. The game-facing part (firing the retainer list callback) cannot be tested
/// without a client, but the intention it rests on can: which retainer, on which venture, and that stopping
/// leaves nothing behind to act on later.
/// </summary>
public class VentureRunnerTargetTests
{
    private static VentureRunner Runner() =>
        new(Mock.Of<IGameGui>(), Mock.Of<IDataManager>(), Mock.Of<IPluginLog>());

    [Fact]
    public void APlainArmServesWhoeverIsAtTheBell()
    {
        var runner = Runner();

        runner.Arm();

        Assert.True(runner.Armed);
        Assert.Equal(-1, runner.TargetIndex);
    }

    [Fact]
    public void ArmingForARetainerRemembersWhichOneAndOnWhat()
    {
        var runner = Runner();

        runner.ArmFor(3, 903);

        Assert.True(runner.Armed);
        Assert.Equal(3, runner.TargetIndex);
        Assert.Equal(903u, runner.WantedTaskId);
        Assert.Contains("#3", runner.Status);
    }

    /// <summary>
    /// A stopped run carries no target and no planned venture: the next arm must not inherit the last press,
    /// which is the same rule the request list keeps about intention.
    /// </summary>
    [Fact]
    public void StoppingLeavesNoTargetBehind()
    {
        var runner = Runner();
        runner.ArmFor(2, 768);

        runner.Stop("stopped");

        Assert.False(runner.Armed);
        Assert.Equal(-1, runner.TargetIndex);
        Assert.Equal(0u, runner.WantedTaskId);
    }
}
