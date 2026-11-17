using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Tweaks;

/// <summary>How one character's game window takes part in the dock.</summary>
public enum DockMode
{
    Off = 0,

    /// <summary>Always in a small dock slot — a bot nobody plays.</summary>
    Docked = 1,

    /// <summary>In a small dock slot until clicked, then swapped into the main region.</summary>
    Swap = 2,
}

/// <summary>Where Charon last put this client's window.</summary>
public enum DockPlace
{
    /// <summary>Not placed yet this session.</summary>
    None,
    Main,
    Dock,
}

public enum DockMove
{
    Stay,
    ToMain,
    ToDock,
}

/// <summary>A rectangle on the desktop, in pixels.</summary>
public readonly record struct Box(int Left, int Top, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= Left && x < Left + Width && y >= Top && y < Top + Height;

    public (int X, int Y) Centre => (Left + Width / 2, Top + Height / 2);
}

/// <summary>The main region and the dock slots, as outer window rectangles.</summary>
public sealed record DockLayout(Box Main, IReadOnlyList<Box> Slots);

/// <summary>One screen's usable area (the taskbar left out).</summary>
public readonly record struct Screen(Box Work, bool Primary);

/// <summary>
/// A window dock for several game clients, after ISBoxer's window layouts (Lavish Software; closed
/// source — only the behaviour, read from its wiki): one MAIN region and a set of small DOCK slots. A
/// <see cref="DockMode.Swap"/> client sits in a slot until it is clicked, then takes the main region,
/// and the client that was there drops into the slot left free (ISBoxer's "roaming slot"). A
/// <see cref="DockMode.Docked"/> client stays small.
///
/// Unlike ISBoxer's Instant Swap, which keeps every window rendering at the main region's size, the
/// windows are really resized: a small picture is what frees video memory (measured 2026-11-15:
/// about 215 MB less at 960x540 against 1920x1009), at the cost of a short hitch on each swap.
///
/// Every client runs this for its own window only. They never talk: each one reads where the other
/// clients' windows ARE, so there is nothing to keep in step and nothing to go stale.
/// </summary>
public static class WindowDock
{
    public const int MinSlots = 1;
    public const int MaxSlots = 6;

    /// <summary>On one screen the dock strip takes this fraction of its width, down the right side.</summary>
    public const int StripDivisor = 4;

    public static int ClampSlots(int slots) => Math.Clamp(slots, MinSlots, MaxSlots);

    /// <summary>Screen choice "pick for me".</summary>
    public const int Auto = -1;

    /// <summary>
    /// The layout for this PC's screens, which must be in left-to-right order (that order is what the
    /// two choices index, so "screen 3" means the same on every client). Auto puts the main region on
    /// the primary screen and the dock on the screen to its right, else on any other screen, else on
    /// the same one. A choice that no longer exists (a screen was unplugged) falls back to auto. With
    /// both on one screen the dock is a strip; otherwise the main region is its whole screen.
    /// Null when there are no screens to lay out on.
    /// </summary>
    public static DockLayout? Layout(IReadOnlyList<Screen> screens, int mainScreen, int dockScreen, int slots)
    {
        if (screens.Count == 0)
            return null;

        var main = mainScreen >= 0 && mainScreen < screens.Count ? mainScreen : PrimaryIndex(screens);
        int dock;
        if (dockScreen >= 0 && dockScreen < screens.Count)
            dock = dockScreen;
        else if (main + 1 < screens.Count)
            dock = main + 1;
        else if (main > 0)
            dock = main - 1;
        else
            dock = main;

        return dock == main
            ? Strip(screens[main].Work, slots)
            : TwoScreens(screens[main].Work, screens[dock].Work, slots);
    }

    private static int PrimaryIndex(IReadOnlyList<Screen> screens)
    {
        for (var i = 0; i < screens.Count; i++)
            if (screens[i].Primary)
                return i;
        return 0;
    }

    /// <summary>"Screen 2 (centre), 1920x1032" — for the settings combo.</summary>
    public static string ScreenLabel(IReadOnlyList<Screen> screens, int index)
    {
        var where = screens.Count switch
        {
            1 => string.Empty,
            2 => index == 0 ? " (left)" : " (right)",
            3 => index == 0 ? " (left)" : index == 1 ? " (centre)" : " (right)",
            _ => string.Empty,
        };
        var work = screens[index].Work;
        return $"Screen {index + 1}{where}{(screens[index].Primary ? ", primary" : string.Empty)}, {work.Width}x{work.Height}";
    }

    /// <summary>One screen: the main region on the left, the slots stacked down the right. The last
    /// slot takes the rounding remainder so the strip reaches the bottom.</summary>
    public static DockLayout Strip(Box work, int slots)
    {
        slots = ClampSlots(slots);
        var strip = work.Width / StripDivisor;
        var height = work.Height / slots;
        var boxes = new List<Box>();
        for (var i = 0; i < slots; i++)
        {
            var top = work.Top + height * i;
            boxes.Add(new Box(work.Left + work.Width - strip, top, strip,
                i == slots - 1 ? work.Top + work.Height - top : height));
        }

        return new DockLayout(new Box(work.Left, work.Top, work.Width - strip, work.Height), boxes);
    }

    /// <summary>Two screens: the main region is the whole first screen, the slots tile the second —
    /// one slot fills it, more go two across, filled row by row from the top left.</summary>
    public static DockLayout TwoScreens(Box primary, Box second, int slots)
    {
        slots = ClampSlots(slots);
        var columns = slots == 1 ? 1 : 2;
        var rows = (slots + columns - 1) / columns;
        var width = second.Width / columns;
        var height = second.Height / rows;
        var boxes = new List<Box>();
        for (var i = 0; i < slots; i++)
            boxes.Add(new Box(second.Left + width * (i % columns), second.Top + height * (i / columns), width, height));
        return new DockLayout(primary, boxes);
    }

    /// <summary>The first slot no other client's window is centred in, or null when all are taken.</summary>
    public static int? FreeSlot(DockLayout layout, IEnumerable<Box> others)
    {
        var centres = others.Select(o => o.Centre).ToList();
        for (var i = 0; i < layout.Slots.Count; i++)
            if (!centres.Any(c => layout.Slots[i].Contains(c.X, c.Y)))
                return i;
        return null;
    }

    /// <summary>Another client's window is centred in the main region.</summary>
    public static bool MainTaken(DockLayout layout, IEnumerable<Box> others) =>
        others.Any(o => layout.Main.Contains(o.Centre.X, o.Centre.Y));

    /// <summary>
    /// Two clients picked the same slot in the same moment (every client reloading at once does it).
    /// The one with the LOWER process id keeps it; this is true for the one that must move.
    /// </summary>
    public static bool MustYieldSlot(DockLayout layout, int slot, int processId,
        IEnumerable<(int ProcessId, Box Bounds)> others)
    {
        if (slot < 0 || slot >= layout.Slots.Count)
            return true; // the dock has fewer slots now than when this one was taken
        var mine = layout.Slots[slot];
        return others.Any(o => o.ProcessId < processId && mine.Contains(o.Bounds.Centre.X, o.Bounds.Centre.Y));
    }

    /// <summary>What this client's window should do now.</summary>
    /// <param name="focused">This client's window has the keyboard focus.</param>
    /// <param name="mainTakenByOther">Another client's window sits in the main region.</param>
    public static DockMove Decide(DockMode mode, DockPlace place, bool focused, bool mainTakenByOther)
    {
        switch (mode)
        {
            case DockMode.Docked:
                return place == DockPlace.Dock ? DockMove.Stay : DockMove.ToDock;

            case DockMode.Swap:
                if (focused)
                    return place == DockPlace.Main ? DockMove.Stay : DockMove.ToMain;

                // Out of focus. A click on another app, or on a docked bot that stays small, changes
                // nothing: the main window only gives way to a client that has taken its place.
                if (place == DockPlace.None)
                    return DockMove.ToDock;
                return place == DockPlace.Main && mainTakenByOther ? DockMove.ToDock : DockMove.Stay;

            default:
                return DockMove.Stay;
        }
    }
}
