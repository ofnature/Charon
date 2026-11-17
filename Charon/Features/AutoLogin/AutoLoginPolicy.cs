using System;

namespace Charon.Features.AutoLogin;

/// <summary>What the client is showing, as far as logging back in is concerned.</summary>
public enum LoginScreen
{
    /// <summary>Connecting, loading, a queue, or anything else there is nothing to press on.</summary>
    Other,
    LoggedIn,

    /// <summary>A lobby or disconnect error box with an OK button.</summary>
    Error,

    /// <summary>The title menu, ready for a press.</summary>
    Title,
    CharacterSelect,

    /// <summary>The "log in with this character?" Yes/No over the character list.</summary>
    Confirm,
}

public enum LoginAction
{
    None,
    DismissError,
    PressStart,
    SelectCharacter,
    Confirm,

    /// <summary>The character is back in the world; the run is over.</summary>
    Finished,

    /// <summary>The run stopped without logging in; the reason says why.</summary>
    GaveUp,
}

public readonly record struct LoginStep(LoginAction Action, string Reason);

/// <summary>
/// Logging a character back in after a disconnect: dismiss the error, press Start, pick the same
/// character, confirm. One step per call, decided from what is on screen right now.
///
/// The idea is VIWI's AutoLogin (VeraNala; no licence, so nothing of it was copied). The design
/// difference is deliberate: VIWI looks the character's data centre up in the game's World sheet and
/// picks it on the data centre map, and since the 2026-11-03 game data that lookup is WRONG (a column
/// was added; Maduin reads Aether 4 instead of Dynamis 11). This never looks a data centre up: Start
/// reconnects to the data centre the client was on, and the character is then found in the list by
/// CONTENT ID. If it is not on the list, the run stops and says so rather than logging in somewhere else.
///
/// A run is armed only by a real disconnect (see <see cref="IsDisconnect"/>), never by a logout the
/// player asked for.
/// </summary>
public sealed class AutoLoginPolicy
{
    /// <summary>Lobby errors come in runs during an outage; this is how long a run keeps trying.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(6);

    /// <summary>How long the character list may lack the character before the run stops.</summary>
    public static readonly TimeSpan CharacterListWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the character list may stay up after the login was confirmed before the character is
    /// chosen again. The login itself — loading, or a queue — happens behind that same list, so the
    /// list still being there says nothing for a while. Seen live on 2026-11-16: 35 seconds from the
    /// confirm to being in the world, during which the character was clicked eleven more times.
    /// </summary>
    public static readonly TimeSpan LoginWait = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan ErrorPace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartPace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CharacterPace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConfirmPace = TimeSpan.FromSeconds(1);

    private DateTime _startedUtc;
    private DateTime _nextClickUtc;
    private DateTime _retryNotBeforeUtc;
    private DateTime? _listSinceUtc;
    private DateTime? _confirmedUtc;
    private bool _inErrorEpisode;

    public bool Armed { get; private set; }

    /// <summary>Error boxes dismissed this run, counted once per run of boxes.</summary>
    public int Errors { get; private set; }

    /// <summary>
    /// Logout codes that mean the connection was lost rather than that the player left: 90001, 90002,
    /// 90006, 90007 (the set VIWI watches; 90001 was seen in the user's own log on 2026-11-15).
    /// </summary>
    public static bool IsDisconnect(int code) => code is 90001 or 90002 or 90006 or 90007;

    /// <summary>The wait before pressing Start again after the n-th error: 5s, then 15s more per error,
    /// capped at two minutes — a server that is down is not helped by being asked every second.</summary>
    public static TimeSpan Backoff(int errors) =>
        errors <= 1 ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(Math.Min(15 * (errors - 1), 120));

    public void Arm(DateTime nowUtc)
    {
        Armed = true;
        Errors = 0;
        _startedUtc = nowUtc;
        _nextClickUtc = _retryNotBeforeUtc = nowUtc;
        _listSinceUtc = null;
        _confirmedUtc = null;
        _inErrorEpisode = false;
    }

    public void Disarm() => Armed = false;

    /// <param name="characterListed">The character we were is in the character list on screen.</param>
    public LoginStep Next(LoginScreen screen, bool characterListed, DateTime nowUtc)
    {
        if (!Armed)
            return new LoginStep(LoginAction.None, "idle");

        if (screen == LoginScreen.LoggedIn)
        {
            Armed = false;
            return new LoginStep(LoginAction.Finished, "logged back in");
        }

        if (nowUtc - _startedUtc > GiveUpAfter)
        {
            Armed = false;
            return new LoginStep(LoginAction.GaveUp, $"gave up after {GiveUpAfter.TotalHours:0} hours of trying");
        }

        if (screen != LoginScreen.CharacterSelect)
            _listSinceUtc = null;

        switch (screen)
        {
            case LoginScreen.Error:
                _confirmedUtc = null;
                if (!_inErrorEpisode)
                {
                    _inErrorEpisode = true;
                    Errors++;
                    _retryNotBeforeUtc = nowUtc + Backoff(Errors);
                }

                return Paced(LoginAction.DismissError, ErrorPace, nowUtc, $"dismissing error #{Errors}");

            case LoginScreen.Title:
                _inErrorEpisode = false;
                _confirmedUtc = null;
                if (nowUtc < _retryNotBeforeUtc)
                    return new LoginStep(LoginAction.None,
                        $"waiting {(_retryNotBeforeUtc - nowUtc).TotalSeconds:0}s before trying again (error #{Errors})");
                return Paced(LoginAction.PressStart, StartPace, nowUtc, "pressing Start");

            case LoginScreen.CharacterSelect:
                _inErrorEpisode = false;
                if (_confirmedUtc is { } confirmed && nowUtc - confirmed < LoginWait)
                    return new LoginStep(LoginAction.None, "confirmed — waiting for the game to log in");
                if (characterListed)
                    return Paced(LoginAction.SelectCharacter, CharacterPace, nowUtc, "choosing the character");

                _listSinceUtc ??= nowUtc;
                if (nowUtc - _listSinceUtc.Value < CharacterListWait)
                    return new LoginStep(LoginAction.None, "waiting for the character list");

                Armed = false;
                return new LoginStep(LoginAction.GaveUp,
                    "the character is not on this character list — the game opened a different data centre or account");

            case LoginScreen.Confirm:
                var confirm = Paced(LoginAction.Confirm, ConfirmPace, nowUtc, "confirming the login");
                if (confirm.Action == LoginAction.Confirm)
                    _confirmedUtc = nowUtc;
                return confirm;

            default:
                return new LoginStep(LoginAction.None, "waiting for the game");
        }
    }

    private LoginStep Paced(LoginAction action, TimeSpan pace, DateTime nowUtc, string reason)
    {
        if (nowUtc < _nextClickUtc)
            return new LoginStep(LoginAction.None, reason);
        _nextClickUtc = nowUtc + pace;
        return new LoginStep(action, reason);
    }
}
