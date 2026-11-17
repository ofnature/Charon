using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Charon.Features.Tweaks;

namespace Charon.Services.Game;

/// <summary>
/// Resizing the game's own window, and putting it back. The calls were checked on a throwaway window
/// (2026-11-15): <c>SW_SHOWNOACTIVATE</c> takes a window out of maximised without giving it the focus,
/// <c>SetWindowPos</c> with the frame added gives the exact picture size, and
/// <c>SetWindowPlacement</c> restores a maximised window. In game the same day the memory probe shrank
/// a client to 1280x720 and 960x540 and back: the game reallocates its render targets on a resize,
/// which is what frees the video memory.
/// </summary>
public static class GameWindow
{
    private const int ShowNoActivate = 4;
    private const int ShowRestore = 9;
    private const uint PrimaryMonitor = 0x1;
    private const uint NoMove = 0x0002;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>Windows' WINDOWPLACEMENT (44 bytes): where a window is and whether it is maximised.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Placement
    {
        public int Length, Flags, ShowCmd, MinX, MinY, MaxX, MaxY;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint window, ref Placement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(nint window, ref Placement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    private delegate bool MonitorProc(nint monitor, nint deviceContext, ref Rect rect, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint deviceContext, nint clip, MonitorProc proc, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    public static bool IsMinimised(nint window) => window != 0 && IsIconic(window);

    /// <summary>The size of the picture inside the window's frame, or (0, 0) when it cannot be read.</summary>
    public static (int Width, int Height) PictureSize(nint window) =>
        window != 0 && GetClientRect(window, out var rect) ? (rect.Right, rect.Bottom) : (0, 0);

    /// <summary>The window's outer rectangle on the desktop, frame included.</summary>
    public static Box? Bounds(nint window) =>
        window != 0 && GetWindowRect(window, out var rect)
            ? new Box(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : null;

    /// <summary>Every screen's usable area (taskbar left out), in LEFT-TO-RIGHT order — the order the
    /// dock's screen choices index, and the same on every client of this PC.</summary>
    public static List<Screen> Screens()
    {
        var found = new List<Screen>();
        MonitorProc collect = (nint monitor, nint _, ref Rect _, nint _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(monitor, ref info))
                found.Add(new Screen(
                    new Box(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top),
                    (info.Flags & PrimaryMonitor) != 0));
            return true;
        };
        EnumDisplayMonitors(0, 0, collect, 0);
        GC.KeepAlive(collect);
        return found.OrderBy(s => s.Work.Left).ThenBy(s => s.Work.Top).ToList();
    }

    /// <summary>Moves and resizes the window to an outer rectangle, without giving it the focus.</summary>
    public static bool SetBounds(nint window, Box box)
    {
        if (window == 0)
            return false;
        LeaveMaximised(window);
        return SetWindowPos(window, 0, box.Left, box.Top, box.Width, box.Height, NoZOrder | NoActivate);
    }

    /// <summary>Where the window is now, to hand to <see cref="Restore"/> later.</summary>
    public static Placement? Save(nint window)
    {
        var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
        return window != 0 && GetWindowPlacement(window, ref placement) ? placement : null;
    }

    /// <summary>Resizes the window so its picture is the given size, without giving it the focus when
    /// that can be avoided. A maximised window is taken out of maximised first — it cannot be resized.</summary>
    public static bool SetPictureSize(nint window, int width, int height)
    {
        if (window == 0)
            return false;
        LeaveMaximised(window);

        if (!GetWindowRect(window, out var outer) || !GetClientRect(window, out var inner))
            return false;
        var frameWidth = outer.Right - outer.Left - inner.Right;
        var frameHeight = outer.Bottom - outer.Top - inner.Bottom;
        return SetWindowPos(window, 0, 0, 0, width + frameWidth, height + frameHeight, NoMove | NoZOrder | NoActivate);
    }

    /// <summary>A maximised window cannot be moved or resized. Prefers the way that takes no focus.</summary>
    private static void LeaveMaximised(nint window)
    {
        if (!IsZoomed(window))
            return;
        ShowWindow(window, ShowNoActivate);
        if (IsZoomed(window))
            ShowWindow(window, ShowRestore);
    }

    /// <summary>Puts the window back as saved. Re-maximising brings it to the front, so the window
    /// that had the focus is given it back.</summary>
    public static void Restore(nint window, Placement placement)
    {
        if (window == 0)
            return;
        var front = GetForegroundWindow();
        SetWindowPlacement(window, ref placement);
        if (front != 0 && front != window && GetForegroundWindow() == window)
            SetForegroundWindow(front);
    }
}
