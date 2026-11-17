using System;
using Charon.Features.AutoLogin;

namespace Charon.Tests.Features.AutoLogin;

public sealed class AutoLoginPolicyTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 3, 0, 0, DateTimeKind.Utc);

    private static AutoLoginPolicy Armed()
    {
        var policy = new AutoLoginPolicy();
        policy.Arm(T0);
        return policy;
    }

    [Theory]
    [InlineData(90001, true)]
    [InlineData(90002, true)]
    [InlineData(90006, true)]
    [InlineData(90007, true)]
    [InlineData(0, false)]     // an ordinary logout
    [InlineData(90003, false)]
    public void OnlyALostConnection_CountsAsADisconnect(int code, bool expected)
    {
        Assert.Equal(expected, AutoLoginPolicy.IsDisconnect(code));
    }

    [Fact]
    public void Unarmed_DoesNothing_WhateverIsOnScreen()
    {
        var policy = new AutoLoginPolicy();
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.Title, true, T0).Action);
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.CharacterSelect, true, T0).Action);
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.Confirm, true, T0).Action);
    }

    [Fact]
    public void TheWholeRun_ErrorThenStartThenCharacterThenConfirmThenIn()
    {
        var policy = Armed();
        var t = T0;

        Assert.Equal(LoginAction.DismissError, policy.Next(LoginScreen.Error, false, t).Action);
        // The disconnect raises two boxes in a row; the second is dismissed a second later.
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.Error, false, t.AddMilliseconds(300)).Action);
        Assert.Equal(LoginAction.DismissError, policy.Next(LoginScreen.Error, false, t = t.AddSeconds(1.1)).Action);
        Assert.Equal(1, policy.Errors); // one run of boxes is one error

        // The title: a short wait after the error, then Start.
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.Title, false, t = t.AddSeconds(1)).Action);
        Assert.Equal(LoginAction.PressStart, policy.Next(LoginScreen.Title, false, t = T0.AddSeconds(6)).Action);
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.Other, false, t = t.AddSeconds(1)).Action);

        Assert.Equal(LoginAction.SelectCharacter, policy.Next(LoginScreen.CharacterSelect, true, t = t.AddSeconds(6)).Action);
        Assert.Equal(LoginAction.Confirm, policy.Next(LoginScreen.Confirm, true, t = t.AddSeconds(4)).Action);
        Assert.Equal(LoginAction.Finished, policy.Next(LoginScreen.LoggedIn, false, t.AddSeconds(20)).Action);
        Assert.False(policy.Armed);
    }

    [Fact]
    public void StartIsNotHammered_WhileTheTitleIsStillUp()
    {
        var policy = Armed();
        Assert.Equal(LoginAction.PressStart, policy.Next(LoginScreen.Title, false, T0).Action);
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.Title, false, T0.AddSeconds(2)).Action);
        Assert.Equal(LoginAction.PressStart, policy.Next(LoginScreen.Title, false, T0.AddSeconds(5.1)).Action);
    }

    [Fact]
    public void RepeatedLobbyErrors_BackOff_UpToTwoMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), AutoLoginPolicy.Backoff(1));
        Assert.Equal(TimeSpan.FromSeconds(15), AutoLoginPolicy.Backoff(2));
        Assert.Equal(TimeSpan.FromSeconds(60), AutoLoginPolicy.Backoff(5));
        Assert.Equal(TimeSpan.FromSeconds(120), AutoLoginPolicy.Backoff(50));

        var policy = Armed();
        var t = T0;
        policy.Next(LoginScreen.Error, false, t);                       // error 1
        policy.Next(LoginScreen.Title, false, t = t.AddSeconds(6));     // Start
        policy.Next(LoginScreen.Error, false, t = t.AddSeconds(6));     // error 2: wait 15s
        Assert.Equal(2, policy.Errors);
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.Title, false, t.AddSeconds(10)).Action);
        Assert.Equal(LoginAction.PressStart, policy.Next(LoginScreen.Title, false, t.AddSeconds(16)).Action);
    }

    [Fact]
    public void ACharacterMissingFromTheList_StopsTheRun_RatherThanLoggingInElsewhere()
    {
        var policy = Armed();
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.CharacterSelect, false, T0).Action);
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.CharacterSelect, false, T0.AddSeconds(29)).Action);
        var step = policy.Next(LoginScreen.CharacterSelect, false, T0.AddSeconds(31));
        Assert.Equal(LoginAction.GaveUp, step.Action);
        Assert.Contains("not on this character list", step.Reason);
        Assert.False(policy.Armed);
    }

    [Fact]
    public void TheListWait_StartsAgain_EachTimeTheListIsReached()
    {
        var policy = Armed();
        policy.Next(LoginScreen.CharacterSelect, false, T0);
        policy.Next(LoginScreen.Other, false, T0.AddSeconds(20));
        // Back at the list 40s after first seeing it: a fresh 30s, not an instant give-up.
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.CharacterSelect, false, T0.AddSeconds(40)).Action);
        Assert.True(policy.Armed);
    }

    [Fact]
    public void AnOutage_IsGivenUpOn_AfterSixHours()
    {
        var policy = Armed();
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.Other, false, T0.AddHours(5.9)).Action);
        Assert.Equal(LoginAction.GaveUp, policy.Next(LoginScreen.Other, false, T0.AddHours(6.1)).Action);
        Assert.False(policy.Armed);
    }

    [Fact]
    public void Disarm_StopsARunAtOnce()
    {
        var policy = Armed();
        policy.Disarm();
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.Title, true, T0.AddSeconds(30)).Action);
    }

    [Fact]
    public void EveryStep_SaysWhatItIsDoing()
    {
        var policy = Armed();
        Assert.NotEmpty(policy.Next(LoginScreen.Other, false, T0).Reason);
        Assert.NotEmpty(policy.Next(LoginScreen.Error, false, T0).Reason);
        Assert.NotEmpty(policy.Next(LoginScreen.Title, false, T0.AddSeconds(1)).Reason);
    }

    [Fact]
    public void AfterTheConfirm_TheCharacterIsNotClickedAgain_WhileTheGameLogsIn()
    {
        var policy = Armed();
        Assert.Equal(LoginAction.SelectCharacter, policy.Next(LoginScreen.CharacterSelect, true, T0).Action);
        Assert.Equal(LoginAction.Confirm, policy.Next(LoginScreen.Confirm, true, T0.AddSeconds(3)).Action);

        // The list stays on screen while the world loads: 35 seconds of it, seen live.
        for (var second = 4; second <= 40; second += 3)
            Assert.Equal(LoginAction.None, policy.Next(LoginScreen.CharacterSelect, true, T0.AddSeconds(second)).Action);

        Assert.Equal(LoginAction.Finished, policy.Next(LoginScreen.LoggedIn, false, T0.AddSeconds(41)).Action);
    }

    [Fact]
    public void AConfirmThatNeverLogsIn_IsTriedAgain_OnceTheWaitIsOver()
    {
        var policy = Armed();
        policy.Next(LoginScreen.CharacterSelect, true, T0);
        policy.Next(LoginScreen.Confirm, true, T0.AddSeconds(3));

        var after = T0.AddSeconds(3) + AutoLoginPolicy.LoginWait + TimeSpan.FromSeconds(1);

        Assert.Equal(LoginAction.SelectCharacter, policy.Next(LoginScreen.CharacterSelect, true, after).Action);
    }

    [Fact]
    public void AnErrorAfterTheConfirm_EndsTheWait()
    {
        var policy = Armed();
        policy.Next(LoginScreen.CharacterSelect, true, T0);
        policy.Next(LoginScreen.Confirm, true, T0.AddSeconds(3));
        policy.Next(LoginScreen.Error, false, T0.AddSeconds(5));

        Assert.Equal(LoginAction.SelectCharacter, policy.Next(LoginScreen.CharacterSelect, true, T0.AddSeconds(20)).Action);
    }
}
