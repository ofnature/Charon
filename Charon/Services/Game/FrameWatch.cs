using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Charon.Features.Tweaks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Config;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Charon.Services.Game;

/// <summary>
/// Read-only frame-rate check for the Debug page: how fast this client runs while focused and while
/// in the background, how often it hitches, and what the game's own frame-rate settings are.
///
/// Background frames are what an unattended bot runs on, and the person reading the line has just
/// focused the window, so the two are measured separately and the background figures are kept from
/// before the focus change. Loading screens are skipped — their long frames are not hitches anyone
/// can fix. Nothing here changes a setting.
/// </summary>
public sealed class FrameWatch
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    private static readonly TimeSpan SettingsInterval = TimeSpan.FromSeconds(2);

    private readonly IGameConfig _gameConfig;
    private readonly ICondition _condition;
    private readonly IDalamudPluginInterface _pluginInterface;

    private readonly FrameMeter _focused = new();
    private readonly FrameMeter _background = new();

    private nint _window;
    private bool _skipNext;
    private bool _isFocused;
    private bool _isMinimised;
    private DateTime _nextSettingsUtc;
    private string _settings = "game settings not read yet";

    /// <summary>The game window has been found, so <see cref="IsFocused"/> means something.</summary>
    public bool WindowKnown => _window != 0;

    /// <summary>The game window is the foreground window, as of the last update.</summary>
    public bool IsFocused => _isFocused;

    /// <summary>Chill Frames is loaded, as of the last settings read (every 2s).</summary>
    public bool ChillFramesLoaded { get; private set; }

    public FrameWatch(IGameConfig gameConfig, ICondition condition, IDalamudPluginInterface pluginInterface)
    {
        _gameConfig = gameConfig;
        _condition = condition;
        _pluginInterface = pluginInterface;
    }

    public void Update(IFramework framework, DateTime nowUtc)
    {
        if (_window == 0)
            _window = Process.GetCurrentProcess().MainWindowHandle;

        _isFocused = _window != 0 && GetForegroundWindow() == _window;
        _isMinimised = _window != 0 && IsIconic(_window);

        if (nowUtc >= _nextSettingsUtc)
        {
            _nextSettingsUtc = nowUtc + SettingsInterval;
            _settings = ReadSettings();
        }

        if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51])
        {
            _skipNext = true; // the first frame after the load is part of the load
            return;
        }

        if (_skipNext)
        {
            _skipNext = false;
            return;
        }

        (_isFocused ? _focused : _background).Record(framework.UpdateDelta.TotalMilliseconds, nowUtc);
    }

    public string Status
    {
        get
        {
            var now = DateTime.UtcNow;
            var state = _isMinimised ? "minimised" : _isFocused ? "focused" : "in the background";
            return $"now {state} · background: {Describe(_background, now)} · focused: {Describe(_focused, now)} · {_settings}";
        }
    }

    private static string Describe(FrameMeter meter, DateTime now)
    {
        if (meter.Describe() is not { } text || meter.LastSampleUtc is not { } last)
            return "not seen yet";

        var age = now - last;
        return age < TimeSpan.FromSeconds(2) ? text : $"{text} (as of {FormatAge(age)} ago)";
    }

    private static string FormatAge(TimeSpan age) =>
        age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes}m" : $"{(int)age.TotalSeconds}s";

    private string ReadSettings()
    {
        var inactive = _gameConfig.TryGet(SystemConfigOption.FPSInActive, out bool limitInactive)
            ? (limitInactive ? "on" : "OFF")
            : "?";
        var afk = _gameConfig.TryGet(SystemConfigOption.FPSDownAFK, out bool limitAfk)
            ? (limitAfk ? "on" : "off")
            : "?";
        var threshold = _gameConfig.TryGet(SystemConfigOption.Fps, out uint fps) ? $"raw {fps}" : "?";

        var text = $"game: limit when inactive {inactive}, when AFK {afk}, frame rate threshold {threshold}";

        // Chill Frames turns the game's inactive limit off and replaces it with its own, so the
        // setting above stops meaning what it says while Chill Frames is running.
        ChillFramesLoaded = _pluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == "ChillFrames");
        if (ChillFramesLoaded)
            text += " · Chill Frames loaded (it replaces the game's inactive limit)";

        return text;
    }
}
