using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Charon.Features.Tweaks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Charon.Services.Game;

/// <summary>
/// Keeps this client from being logged out for inactivity.
///
/// The mechanism is the client's own: read the three idle accumulators out of the UI module's input
/// timer, and when the client says it has been idle too long, send it a keystroke the way the game
/// expects to receive one — a window message to our own process's window, which works while the window
/// is in the background, which is the only situation this exists for.
///
/// Charon's own, not a port: the plugin this idea comes from (AntiAfkKick, AGPL-3.0) reads the same
/// three timers and sends the same key, but its licence forbids reusing its code, and the mechanism
/// itself is a fact about the client rather than anyone's implementation. Two things it does not do
/// that this does: report the timer it saw, and say so when a nudge fails to reset it.
///
/// The key is a bare left Ctrl, the one key with no action of its own in game — it cannot fire a
/// hotbar, and typed into the chat box it is not a character (unlike the 'A' the QTE solver sends,
/// which is why that one has to park Direct Chat first and this one does not).
/// </summary>
public sealed unsafe class AfkGuard
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int VkLeftControl = 162;

    [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private readonly Func<bool> _enabled;
    private readonly Func<int> _thresholdSeconds;
    private readonly IClientState _clientState;
    private readonly IPluginLog _log;

    private nint _gameWindow;
    private DateTime? _lastNudgeUtc;
    private double _timerAtLastNudge;
    private int _stuckNudges;

    public AfkGuard(
        Func<bool> enabled,
        Func<int> thresholdSeconds,
        IClientState clientState,
        IPluginLog log)
    {
        _enabled = enabled;
        _thresholdSeconds = thresholdSeconds;
        _clientState = clientState;
        _log = log;
    }

    /// <summary>The last decision, in operator words — including the idle timer it saw.</summary>
    public string Status { get; private set; } = "off";

    /// <summary>The worst of the client's three idle timers, in seconds, or -1 when unreadable.</summary>
    public double IdleSeconds { get; private set; } = -1;

    /// <summary>How many keystrokes have been sent this session.</summary>
    public int Nudges { get; private set; }

    /// <summary>Nudges in a row that did not reset the client's timer.</summary>
    public int StuckNudges => _stuckNudges;

    public void Update(DateTime nowUtc)
    {
        var timers = ReadTimers();
        IdleSeconds = timers.Count == 0 ? -1 : timers.Max();

        // Progress clears the failure count: if the timer came down at some point, whatever we sent
        // did land, and the count is about the CURRENT stretch of idleness, not the session.
        if (IdleSeconds >= 0 && IdleSeconds < Math.Max(1, _thresholdSeconds()))
            _stuckNudges = 0;

        var since = _lastNudgeUtc is { } last ? (nowUtc - last).TotalSeconds : (double?)null;
        var decision = AfkGuardPolicy.Decide(
            _enabled(), _clientState.IsLoggedIn, timers, _thresholdSeconds(), since, _stuckNudges);

        Status = decision.Reason;

        if (decision.Action == AfkAction.Nudge)
            Nudge(nowUtc);
    }

    /// <summary>Forget the session's nudge history — on logout, a character switch or a config change.</summary>
    public void Reset()
    {
        _lastNudgeUtc = null;
        _timerAtLastNudge = 0;
        _stuckNudges = 0;
        IdleSeconds = -1;
        Status = "reset";
    }

    private void Nudge(DateTime nowUtc)
    {
        var window = GameWindow();
        if (window == 0)
        {
            Status = "the game window handle is not available — cannot stay logged in";
            _stuckNudges = AfkGuardPolicy.NudgesBeforeGivingUp;
            return;
        }

        // Did the previous nudge actually land? If the timer has not come down since, this one probably
        // will not either — say so after a few, instead of quietly sending keys forever.
        if (_lastNudgeUtc != null && IdleSeconds >= _timerAtLastNudge)
            _stuckNudges++;

        PostMessage(window, WmKeyDown, VkLeftControl, 0);
        PostMessage(window, WmKeyUp, VkLeftControl, 0);

        _lastNudgeUtc = nowUtc;
        _timerAtLastNudge = IdleSeconds;
        Nudges++;

        _log.Debug("[AfkGuard] nudge {0} at {1:0}s idle", Nudges, IdleSeconds);
    }

    private nint GameWindow()
    {
        if (_gameWindow != 0)
            return _gameWindow;

        _gameWindow = Process.GetCurrentProcess().MainWindowHandle;
        return _gameWindow;
    }

    /// <summary>
    /// The client's three idle accumulators. Read-only, and fail-open: a client that is between areas or
    /// not fully up reports nothing, and the guard says it cannot read them rather than inventing a zero.
    /// </summary>
    private static List<double> ReadTimers()
    {
        try
        {
            var ui = UIModule.Instance();
            if (ui == null)
                return [];

            var module = ui->GetInputTimerModule();
            if (module == null)
                return [];

            return [module->AfkTimer, module->ContentInputTimer, module->InputTimer];
        }
        catch (Exception)
        {
            return [];
        }
    }
}
