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

    /// <summary>Korha, 2026-11-25: lobby error 13001 every ~4s for two hours, each box closed at once.</summary>
    [Fact]
    public void ErrorBoxesThatKeepComingBack_AreLeftUpLonger_ThenLeftAlone()
    {
        Assert.Equal(TimeSpan.Zero, AutoLoginPolicy.RepeatWait(2));
        Assert.Equal(TimeSpan.FromSeconds(15), AutoLoginPolicy.RepeatWait(3));
        Assert.Equal(TimeSpan.FromSeconds(30), AutoLoginPolicy.RepeatWait(4));

        var policy = Armed();
        var t = T0;
        Assert.Equal(LoginAction.DismissError, policy.Next(LoginScreen.Error, false, t).Action);     // box 1
        policy.Next(LoginScreen.Other, false, t = t.AddSeconds(1));                                   // the game tries again
        Assert.Equal(LoginAction.DismissError, policy.Next(LoginScreen.Error, false, t = t.AddSeconds(3)).Action); // box 2: at once
        policy.Next(LoginScreen.Other, false, t = t.AddSeconds(1));

        // Box 3: left up 15s.
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.Error, false, t = t.AddSeconds(3)).Action);
        Assert.Contains("stays up", policy.Next(LoginScreen.Error, false, t.AddSeconds(14)).Reason);
        Assert.Equal(LoginAction.DismissError, policy.Next(LoginScreen.Error, false, t = t.AddSeconds(15.1)).Action);
        Assert.Equal(1, policy.Errors); // still one error: the title never came back

        // Boxes 4 to 6, each closed once its wait is over.
        for (var box = 4; box <= AutoLoginPolicy.MaxErrorBoxesInARow; box++)
        {
            policy.Next(LoginScreen.Other, false, t = t.AddSeconds(1));
            Assert.Equal(LoginAction.None, policy.Next(LoginScreen.Error, false, t = t.AddSeconds(3)).Action);
            t += AutoLoginPolicy.RepeatWait(box) + TimeSpan.FromSeconds(0.1);
            Assert.Equal(LoginAction.DismissError, policy.Next(LoginScreen.Error, false, t).Action);
        }

        // One more: the run stops and the box stays where it is.
        policy.Next(LoginScreen.Other, false, t = t.AddSeconds(1));
        var step = policy.Next(LoginScreen.Error, false, t = t.AddSeconds(3));
        Assert.Equal(LoginAction.GaveUp, step.Action);
        Assert.Contains("lobby", step.Reason);
        Assert.False(policy.Armed);
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.Error, false, t.AddMinutes(5)).Action);
    }

    [Fact]
    public void TheTitleScreenBetweenBoxes_StartsTheCountAgain()
    {
        var policy = Armed();
        var t = T0;
        for (var round = 0; round < 10; round++)
        {
            Assert.Equal(LoginAction.DismissError, policy.Next(LoginScreen.Error, false, t).Action);
            policy.Next(LoginScreen.Title, false, t = t.AddSeconds(1)); // back at the title: an ordinary retry
            t = t.AddMinutes(3);
        }

        Assert.True(policy.Armed);
        Assert.Equal(10, policy.Errors);
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

    // The other PC, 2026-11-20: a lobby error box (5006) of a kind not recognised sat over the
    // character list on four clients, and the character was clicked underneath it every three seconds.

    [Fact]
    public void ACharacterListThatDoesNotAnswer_IsPressedFourTimes_ThenLeftAlone()
    {
        var policy = Armed();
        var t = T0;
        for (var press = 0; press < AutoLoginPolicy.MaxUnansweredPresses; press++)
        {
            Assert.Equal(LoginAction.SelectCharacter, policy.Next(LoginScreen.CharacterSelect, true, t).Action);
            t = t.AddSeconds(3.1);
        }

        var stalled = policy.Next(LoginScreen.CharacterSelect, true, t);
        Assert.Equal(LoginAction.Stalled, stalled.Action);
        Assert.Contains("in the way", stalled.Reason);
        Assert.Equal(1, policy.Stalls);
        Assert.True(policy.Armed);

        // Nothing is pressed for the 15 seconds that follow, then it tries again.
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.CharacterSelect, true, t.AddSeconds(5)).Action);
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.CharacterSelect, true, t.AddSeconds(14)).Action);
        Assert.Equal(LoginAction.SelectCharacter, policy.Next(LoginScreen.CharacterSelect, true, t.AddSeconds(16)).Action);
    }

    [Fact]
    public void EachStall_WaitsLongerThanTheLast()
    {
        var policy = Armed();
        var t = T0;

        TimeSpan WaitAfterStall()
        {
            while (true)
            {
                var step = policy.Next(LoginScreen.CharacterSelect, true, t);
                if (step.Action == LoginAction.Stalled)
                    break;
                t = t.AddSeconds(1);
            }

            var stalledAt = t;
            while (policy.Next(LoginScreen.CharacterSelect, true, t).Action != LoginAction.SelectCharacter)
                t = t.AddSeconds(1);
            return t - stalledAt;
        }

        var first = WaitAfterStall();
        var second = WaitAfterStall();
        var third = WaitAfterStall();

        Assert.True(second > first, $"{first} then {second}");
        Assert.True(third > second, $"{second} then {third}");
    }

    [Fact]
    public void APressThatWasAnswered_DoesNotCountTowardsAStall()
    {
        var policy = Armed();
        var t = T0;
        for (var round = 0; round < 3; round++)
        {
            // Three presses, then the list goes away (the game answered), then it is back.
            for (var press = 0; press < 3; press++)
            {
                Assert.Equal(LoginAction.SelectCharacter, policy.Next(LoginScreen.CharacterSelect, true, t).Action);
                t = t.AddSeconds(3.1);
            }

            Assert.Equal(LoginAction.None, policy.Next(LoginScreen.Other, false, t).Action);
            t = t.AddSeconds(1);
        }

        Assert.Equal(0, policy.Stalls);
    }

    [Fact]
    public void AStartThatDoesNotAnswer_IsPressedFourTimes_ThenLeftAlone()
    {
        var policy = Armed();
        var t = T0;
        for (var press = 0; press < AutoLoginPolicy.MaxUnansweredPresses; press++)
        {
            Assert.Equal(LoginAction.PressStart, policy.Next(LoginScreen.Title, false, t).Action);
            t = t.AddSeconds(5.1);
        }

        Assert.Equal(LoginAction.Stalled, policy.Next(LoginScreen.Title, false, t).Action);
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.Title, false, t.AddSeconds(10)).Action);
        Assert.Equal(LoginAction.PressStart, policy.Next(LoginScreen.Title, false, t.AddSeconds(16)).Action);
    }

    [Fact]
    public void WithABoxUpThatIsNotKnown_NothingIsPressed_AndTheRunGoesOn()
    {
        var policy = Armed();

        for (var second = 0; second < 600; second += 5)
        {
            var step = policy.Next(LoginScreen.Blocked, true, T0.AddSeconds(second));
            Assert.Equal(LoginAction.None, step.Action);
            Assert.Contains("not pressing anything", step.Reason);
        }

        Assert.True(policy.Armed);
        Assert.Equal(0, policy.Stalls);

        // The box goes away by itself (a queue, say) and the character is in.
        Assert.Equal(LoginAction.Finished, policy.Next(LoginScreen.LoggedIn, false, T0.AddSeconds(601)).Action);
    }

    [Fact]
    public void AKnownNotice_IsClosed_AndTheCharacterIsThenChosen()
    {
        var policy = Armed();

        Assert.Equal(LoginAction.DismissNotice, policy.Next(LoginScreen.Notice, true, T0).Action);
        // Paced: not pressed again within the second.
        Assert.Equal(LoginAction.None, policy.Next(LoginScreen.Notice, true, T0.AddMilliseconds(250)).Action);

        Assert.Equal(LoginAction.SelectCharacter, policy.Next(LoginScreen.CharacterSelect, true, T0.AddSeconds(5)).Action);
        Assert.Equal(0, policy.Stalls);
    }

    [Fact]
    public void ANoticeThatWillNotClose_IsPressedFourTimes_ThenLeftAlone()
    {
        var policy = Armed();
        var presses = 0;

        for (var second = 0; second < 120; second += 2)
        {
            var step = policy.Next(LoginScreen.Notice, true, T0.AddSeconds(second));
            if (step.Action == LoginAction.DismissNotice)
                presses++;
        }

        Assert.Equal(AutoLoginPolicy.MaxUnansweredPresses, presses);
        Assert.True(policy.Armed);
        Assert.Contains("not pressing anything", policy.Next(LoginScreen.Notice, true, T0.AddSeconds(130)).Reason);
    }

    [Fact]
    public void ANoticeThatComesBackLater_IsClosedAgain()
    {
        var policy = Armed();
        for (var second = 0; second < 20; second += 2)
            policy.Next(LoginScreen.Notice, true, T0.AddSeconds(second));

        policy.Next(LoginScreen.Other, true, T0.AddSeconds(30)); // it went away

        Assert.Equal(LoginAction.DismissNotice, policy.Next(LoginScreen.Notice, true, T0.AddSeconds(40)).Action);
    }

    [Fact]
    public void AfterABoxThatIsNotKnown_TheCharacterIsChosenAsUsual()
    {
        var policy = Armed();
        policy.Next(LoginScreen.Blocked, true, T0);

        Assert.Equal(LoginAction.SelectCharacter, policy.Next(LoginScreen.CharacterSelect, true, T0.AddSeconds(30)).Action);
    }
}
