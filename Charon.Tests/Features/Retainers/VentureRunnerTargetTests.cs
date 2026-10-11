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
        Assert.Null(runner.TargetName);
    }

    [Fact]
    public void ArmingForARetainerRemembersWhichOneAndOnWhat()
    {
        var runner = Runner();

        runner.ArmFor("T'sola", 903);

        Assert.True(runner.Armed);
        // By NAME: a position in the retainer array is not a list row once a slot is unpaid or empty.
        Assert.Equal("T'sola", runner.TargetName);
        Assert.Equal(903u, runner.WantedTaskId);
        Assert.Contains("T'sola", runner.Status);
    }

    /// <summary>
    /// A stopped run carries no target and no planned venture: the next arm must not inherit the last press,
    /// which is the same rule the request list keeps about intention.
    /// </summary>
    [Fact]
    public void StoppingLeavesNoTargetBehind()
    {
        var runner = Runner();
        runner.ArmFor("T'sala", 768);

        runner.Stop("stopped");

        Assert.False(runner.Armed);
        Assert.Null(runner.TargetName);
        Assert.Equal(0u, runner.WantedTaskId);
    }
}
