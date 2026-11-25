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

    /// <summary>The lobby has a box up that this does not know how to answer (an error of another
    /// kind, a queue, a notice). Nothing is pressed while it is there.</summary>
    Blocked,

    /// <summary>A lobby box that only informs and is known by its text (see <see cref="LobbyNotice"/>).
    /// Its OK is pressed.</summary>
    Notice,

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

    /// <summary>Press OK on a lobby notice that is known to be safe to close.</summary>
    DismissNotice,
    PressStart,
    SelectCharacter,
    Confirm,

    /// <summary>The character is back in the world; the run is over.</summary>
    Finished,

    /// <summary>The run stopped without logging in; the reason says why.</summary>
    GaveUp,

    /// <summary>The same press went unanswered several times in a row. Nothing is pressed for a
    /// while; the caller should record what is on screen.</summary>
    Stalled,
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

    /// <summary>
    /// How many times in a row Start or the character may be pressed with nothing changing before the
    /// run stops pressing and waits. Seen on another PC on 2026-11-20: a lobby error box (5006) this
    /// did not recognise sat over the character list, and the character was clicked every three
    /// seconds underneath it.
    /// </summary>
    public const int MaxUnansweredPresses = 4;

    /// <summary>
    /// How many error boxes may come up in a row, with no title screen between them, before the run
    /// stops closing them. Seen on 2026-11-25 (Korha's client, lobby error 13001 "The lobby server
    /// connection has encountered an error."): each close made the game try the lobby again at once,
    /// and fail, so a new box came up every four seconds for two hours and the screen flickered the
    /// whole time. The other client on the same PC logged in normally that minute, so the server was up.
    /// </summary>
    public const int MaxErrorBoxesInARow = 6;

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
    private int _boxesInRow;
    private DateTime _dismissNotBeforeUtc;
    private LoginScreen _lastScreen;
    private int _startPresses;
    private int _characterPresses;
    private int _noticePresses;

    /// <summary>Times this run a press went unanswered <see cref="MaxUnansweredPresses"/> times in a row.</summary>
    public int Stalls { get; private set; }

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

    /// <summary>
    /// How long the n-th error box in a row (no title screen between) stays up before it is closed.
    /// The first two are closed at once (a disconnect raises two boxes); from the third on, closing
    /// one only makes the game try again and fail, so each is left up longer: 15s, 30s, 45s, ...
    /// </summary>
    public static TimeSpan RepeatWait(int boxesInRow) =>
        boxesInRow <= 2 ? TimeSpan.Zero : Backoff(boxesInRow - 1);

    public void Arm(DateTime nowUtc)
    {
        Armed = true;
        Errors = 0;
        _startedUtc = nowUtc;
        _nextClickUtc = _retryNotBeforeUtc = nowUtc;
        _listSinceUtc = null;
        _confirmedUtc = null;
        _inErrorEpisode = false;
        _boxesInRow = 0;
        _dismissNotBeforeUtc = nowUtc;
        _lastScreen = LoginScreen.Other;
        _startPresses = _characterPresses = _noticePresses = 0;
        Stalls = 0;
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

        var previous = _lastScreen;
        _lastScreen = screen;

        if (screen != LoginScreen.CharacterSelect)
        {
            _listSinceUtc = null;
            _characterPresses = 0; // the list went away: the last press was answered
        }

        if (screen != LoginScreen.Title)
            _startPresses = 0;
        if (screen != LoginScreen.Notice)
            _noticePresses = 0;

        switch (screen)
        {
            case LoginScreen.Error:
                _confirmedUtc = null;
                if (!_inErrorEpisode)
                {
                    _inErrorEpisode = true;
                    Errors++;
                    _retryNotBeforeUtc = nowUtc + Backoff(Errors);
                    _boxesInRow = 1;
                }
                else if (previous != LoginScreen.Error)
                {
                    // A NEW box, and the title screen never came back since the last one.
                    _boxesInRow++;
                    if (_boxesInRow > MaxErrorBoxesInARow)
                    {
                        Armed = false;
                        return new LoginStep(LoginAction.GaveUp,
                            $"the game kept failing to reach the lobby ({MaxErrorBoxesInARow} error boxes in a row, never back at the title screen) - "
                            + "Charon has stopped closing them; restarting the client is the likely fix");
                    }

                    _dismissNotBeforeUtc = nowUtc + RepeatWait(_boxesInRow);
                }

                if (nowUtc < _dismissNotBeforeUtc)
                    return new LoginStep(LoginAction.None,
                        $"error box {_boxesInRow} in a row with no title screen between - closing it makes the game try again, "
                        + $"so it stays up {(_dismissNotBeforeUtc - nowUtc).TotalSeconds:0}s more");

                return Paced(LoginAction.DismissError, ErrorPace, nowUtc, $"dismissing error #{Errors}");

            case LoginScreen.Title:
                _inErrorEpisode = false;
                _boxesInRow = 0;
                _confirmedUtc = null;
                if (nowUtc < _retryNotBeforeUtc)
                    return Waiting(nowUtc);
                if (_startPresses >= MaxUnansweredPresses)
                    return Stall("Start", nowUtc);

                var start = Paced(LoginAction.PressStart, StartPace, nowUtc, "pressing Start");
                if (start.Action == LoginAction.PressStart)
                    _startPresses++;
                return start;

            case LoginScreen.CharacterSelect:
                _inErrorEpisode = false;
                _boxesInRow = 0;
                if (_confirmedUtc is { } confirmed && nowUtc - confirmed < LoginWait)
                    return new LoginStep(LoginAction.None, "confirmed — waiting for the game to log in");
                if (characterListed)
                {
                    if (nowUtc < _retryNotBeforeUtc)
                        return Waiting(nowUtc);
                    if (_characterPresses >= MaxUnansweredPresses)
                        return Stall("the character list", nowUtc);

                    var choose = Paced(LoginAction.SelectCharacter, CharacterPace, nowUtc, "choosing the character");
                    if (choose.Action == LoginAction.SelectCharacter)
                        _characterPresses++;
                    return choose;
                }

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

            case LoginScreen.Blocked:
                return new LoginStep(LoginAction.None, "the game has a box up that Charon does not know — not pressing anything");

            case LoginScreen.Notice:
                // A notice that will not close is left alone rather than pressed for ever.
                if (_noticePresses >= MaxUnansweredPresses)
                    return new LoginStep(LoginAction.None,
                        $"the game's notice did not close after {MaxUnansweredPresses} presses of OK — not pressing anything");

                var close = Paced(LoginAction.DismissNotice, ErrorPace, nowUtc, "closing the game's notice");
                if (close.Action == LoginAction.DismissNotice)
                    _noticePresses++;
                return close;

            default:
                return new LoginStep(LoginAction.None, "waiting for the game");
        }
    }

    private LoginStep Waiting(DateTime nowUtc)
    {
        var why = Errors > 0 ? $"error #{Errors}" : "no answer from the game";
        return new LoginStep(LoginAction.None,
            $"waiting {(_retryNotBeforeUtc - nowUtc).TotalSeconds:0}s before trying again ({why})");
    }

    /// <summary>The same press went unanswered too often: stop pressing, wait longer each time.</summary>
    private LoginStep Stall(string what, DateTime nowUtc)
    {
        Stalls++;
        var wait = Backoff(Stalls + 1);
        _retryNotBeforeUtc = nowUtc + wait;
        _startPresses = _characterPresses = 0;
        return new LoginStep(LoginAction.Stalled,
            $"{what} did not answer {MaxUnansweredPresses} presses — something is in the way; trying again in {wait.TotalSeconds:0}s");
    }

    private LoginStep Paced(LoginAction action, TimeSpan pace, DateTime nowUtc, string reason)
    {
        if (nowUtc < _nextClickUtc)
            return new LoginStep(LoginAction.None, reason);
        _nextClickUtc = nowUtc + pace;
        return new LoginStep(action, reason);
    }
}
