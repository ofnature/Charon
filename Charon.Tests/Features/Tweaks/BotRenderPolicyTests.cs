using System;
using Charon.Features.Tweaks;

namespace Charon.Tests.Features.Tweaks;

public sealed class BotRenderPolicyTests
{
    private static readonly BotRenderChoice All = new(true, true, true, true, true);
    private static readonly TimeSpan LongAway = TimeSpan.FromMinutes(1);

    [Fact]
    public void Off_ChangesNothing()
    {
        var wanted = BotRenderPolicy.Wanted(All with { Enabled = false }, LongAway, false, 50);
        Assert.Empty(wanted);
    }

    [Fact]
    public void Focused_ChangesNothing()
    {
        Assert.Empty(BotRenderPolicy.Wanted(All, null, false, 50));
    }

    [Fact]
    public void AQuickAltTab_ChangesNothing()
    {
        var wanted = BotRenderPolicy.Wanted(All, BotRenderPolicy.ApplyDelay - TimeSpan.FromMilliseconds(1), false, 50);
        Assert.Empty(wanted);
    }

    [Fact]
    public void InTheBackground_TurnsEverythingDown()
    {
        var wanted = BotRenderPolicy.Wanted(All, LongAway, false, 50);
        Assert.Equal(BotRenderPolicy.FrameCapOn, wanted[RenderOption.FrameCap]);
        Assert.Equal(BotRenderPolicy.DisplayLimitMinimum, wanted[RenderOption.DisplayLimit]);
        Assert.Equal(50u, wanted[RenderOption.Resolution]);
    }

    [Fact]
    public void InCombat_TheFrameCapLifts_TheRestStays()
    {
        var wanted = BotRenderPolicy.Wanted(All, LongAway, true, 50);
        Assert.False(wanted.ContainsKey(RenderOption.FrameCap));
        Assert.True(wanted.ContainsKey(RenderOption.DisplayLimit));
        Assert.True(wanted.ContainsKey(RenderOption.Resolution));
    }

    [Fact]
    public void InCombat_TheCapStays_WhenNotAskedToLift()
    {
        var wanted = BotRenderPolicy.Wanted(All with { LiftCapInCombat = false }, LongAway, true, 50);
        Assert.True(wanted.ContainsKey(RenderOption.FrameCap));
    }

    [Fact]
    public void EachPart_CanBeLeftOut()
    {
        var wanted = BotRenderPolicy.Wanted(
            All with { CapFrameRate = false, FewerCharacters = false }, LongAway, false, 50);
        Assert.Single(wanted);
        Assert.True(wanted.ContainsKey(RenderOption.Resolution));
    }

    [Fact]
    public void AnUnreadableResolutionFloor_LeavesResolutionAlone()
    {
        var wanted = BotRenderPolicy.Wanted(All, LongAway, false, null);
        Assert.False(wanted.ContainsKey(RenderOption.Resolution));
        Assert.Equal(2, wanted.Count);
    }

    [Fact]
    public void ASettingSomeoneChangedSince_IsNotPutBack()
    {
        Assert.True(BotRenderPolicy.ShouldRestore(current: 4, applied: 4));
        Assert.False(BotRenderPolicy.ShouldRestore(current: 2, applied: 4));
    }

    [Fact]
    public void TheSlider_SetsTheResolution()
    {
        var choice = All with { ResolutionPercent = 70 };
        Assert.Equal(70u, BotRenderPolicy.Wanted(choice, LongAway, false, 50)[RenderOption.Resolution]);
        Assert.Equal(0.7f, BotRenderPolicy.LiveResolution(choice));
    }

    [Fact]
    public void BelowTheGamesFloor_TheOptionStopsAtTheFloor_TheRendererDoesNot()
    {
        var choice = All with { ResolutionPercent = 30 };
        Assert.Equal(50u, BotRenderPolicy.Wanted(choice, LongAway, false, 50)[RenderOption.Resolution]);
        Assert.Equal(0.3f, BotRenderPolicy.LiveResolution(choice));
    }

    [Fact]
    public void ANonsenseResolution_IsClamped()
    {
        Assert.Equal(BotRenderPolicy.MinResolutionPercent / 100f,
            BotRenderPolicy.LiveResolution(All with { ResolutionPercent = 0 }));
        Assert.Equal(1f, BotRenderPolicy.LiveResolution(All with { ResolutionPercent = 400 }));
    }

    [Fact]
    public void Describe_ReadsPlainly()
    {
        Assert.Equal("frame cap on", BotRenderPolicy.Describe(RenderOption.FrameCap, 1));
        Assert.Equal("characters Minimum", BotRenderPolicy.Describe(RenderOption.DisplayLimit, 4));
        Assert.Equal("characters Maximum", BotRenderPolicy.Describe(RenderOption.DisplayLimit, 0));
        Assert.Equal("3D resolution 50%", BotRenderPolicy.Describe(RenderOption.Resolution, 50));
    }
}
