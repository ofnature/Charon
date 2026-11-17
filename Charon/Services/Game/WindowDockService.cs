using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Charon.Features.Tweaks;
using Dalamud.Plugin.Services;

namespace Charon.Services.Game;

/// <summary>
/// Runs the window dock for THIS client's window: into a small dock slot, or into the main region
/// when a swap client is clicked. The rules are <see cref="WindowDock"/>; this reads where the other
/// game clients' windows are and moves our own.
///
/// The window is moved only when the decision changes, never held in place: a window the player
/// drags somewhere else stays there until the next swap. Whatever it was before Charon first moved it
/// is put back when the option is turned off, the character logs out or the plugin unloads.
/// </summary>
public sealed class WindowDockService : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan OthersInterval = TimeSpan.FromMilliseconds(500);

    private readonly CharonConfig _config;
    private readonly Func<ulong> _contentId;
    private readonly FrameWatch _frames;
    private readonly IPluginLog _log;
    private readonly Process _process = Process.GetCurrentProcess();

    private DateTime _nextUtc;
    private DateTime _othersUtc;
    private List<(int ProcessId, Box Bounds)> _others = new();
    private ulong _owner;
    private GameWindow.Placement? _saved;
    private DockPlace _place = DockPlace.None;
    private int _slot;

    public WindowDockService(CharonConfig config, Func<ulong> contentId, FrameWatch frames, IPluginLog log)
    {
        _config = config;
        _contentId = contentId;
        _frames = frames;
        _log = log;
    }

    public string Status { get; private set; } = "not run yet";

    public void Dispose() => Release("the plugin unloaded");

    public void Update(DateTime nowUtc)
    {
        if (nowUtc < _nextUtc)
            return;
        _nextUtc = nowUtc + Interval;

        var contentId = _contentId();
        if (contentId != _owner)
        {
            Release("the character changed");
            _owner = contentId;
        }

        if (contentId == 0)
        {
            Status = "not logged in";
            return;
        }

        var mode = (DockMode)Math.Clamp(_config.RenderProfileFor(contentId).WindowMode, 0, 2);
        if (mode == DockMode.Off)
        {
            Release("turned off");
            Status = "off for this character";
            return;
        }

        var window = _process.MainWindowHandle;
        var slots = WindowDock.ClampSlots(_config.WindowDockSlots);
        if (window == 0 || WindowDock.Layout(GameWindow.Screens(), _config.WindowDockMainScreen,
                _config.WindowDockDockScreen, slots) is not { } layout)
        {
            Status = "waiting to find the game window";
            return;
        }

        if (GameWindow.IsMinimised(window))
        {
            Status = "waiting — the window is minimised";
            return;
        }

        var others = Others(nowUtc);
        var focused = _frames.WindowKnown && _frames.IsFocused;
        var move = WindowDock.Decide(mode, _place, focused, WindowDock.MainTaken(layout, others.Select(o => o.Bounds)));

        // Two clients that docked in the same moment took the same slot; the higher process id moves on.
        if (move == DockMove.Stay && _place == DockPlace.Dock
            && WindowDock.MustYieldSlot(layout, _slot, Environment.ProcessId, others))
            move = DockMove.ToDock;

        switch (move)
        {
            case DockMove.ToMain:
                if (Place(window, layout.Main))
                {
                    _place = DockPlace.Main;
                    _log.Info("Window dock: took the main region");
                }

                break;

            case DockMove.ToDock:
                if (WindowDock.FreeSlot(layout, others.Select(o => o.Bounds)) is not { } free)
                {
                    Status = $"{Describe(mode)} — every one of the {slots} dock slots is taken, so the window stays where it is";
                    return;
                }

                if (Place(window, layout.Slots[free]))
                {
                    _place = DockPlace.Dock;
                    _slot = free;
                    _log.Info($"Window dock: docked in slot {free + 1} of {slots}");
                }

                break;
        }

        var (width, height) = GameWindow.PictureSize(window);
        Status = _place switch
        {
            DockPlace.Main => $"{Describe(mode)} — in the main region, picture {width}x{height}",
            DockPlace.Dock => $"{Describe(mode)} — docked in slot {_slot + 1} of {slots}, picture {width}x{height}",
            _ => $"{Describe(mode)} — the window would not move",
        };
    }

    private static string Describe(DockMode mode) => mode == DockMode.Docked ? "always docked" : "swaps when clicked";

    private bool Place(nint window, Box box)
    {
        // Saved once per session: what the window was before Charon first moved it.
        _saved ??= GameWindow.Save(window);
        return _saved != null && GameWindow.SetBounds(window, box);
    }

    private void Release(string why)
    {
        if (_saved is { } placement)
        {
            GameWindow.Restore(_process.MainWindowHandle, placement);
            _log.Info($"Window dock: window put back ({why})");
        }

        _saved = null;
        _place = DockPlace.None;
    }

    /// <summary>Where the other game clients' windows are, refreshed twice a second: finding another
    /// process's main window walks every window on the desktop.</summary>
    private List<(int ProcessId, Box Bounds)> Others(DateTime nowUtc)
    {
        if (nowUtc < _othersUtc)
            return _others;
        _othersUtc = nowUtc + OthersInterval;

        var found = new List<(int, Box)>();
        try
        {
            foreach (var other in Process.GetProcessesByName(_process.ProcessName))
            {
                using (other)
                {
                    if (other.Id == Environment.ProcessId)
                        continue;
                    var window = other.MainWindowHandle;
                    if (window != 0 && !GameWindow.IsMinimised(window) && GameWindow.Bounds(window) is { } bounds)
                        found.Add((other.Id, bounds));
                }
            }
        }
        catch (Exception ex)
        {
            // A client closing mid-walk; keep the last answer for half a second more.
            _log.Debug(ex, "Window dock: could not read the other clients' windows");
            return _others;
        }

        return _others = found;
    }
}
