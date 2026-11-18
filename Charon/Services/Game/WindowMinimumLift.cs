using System;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace Charon.Services.Game;

/// <summary>
/// Lets the game window go below the game's own minimum size while the window dock wants it small.
///
/// The game refuses a picture under 1024x720: asked for a 960x516 dock slot, a client stayed at
/// 1040x759 and hung over the slots beside and below it (2026-11-16). WHERE it refuses was read off
/// the window, not assumed: the game raises the size itself while answering WM_WINDOWPOSCHANGING
/// (logged: "asked 960x516, the window answered 1040x759"), and it is never asked for its limits the
/// usual way — lowering the minimum in WM_GETMINMAXINFO was tried first and changed nothing, because
/// that message never arrived. So this subclasses the game window, lets everything below answer a
/// resize, and puts back any size that was raised. With <see cref="Lift"/> off it only watches.
///
/// Subclassing is how XASlave's "ignore minimum window size" option does it (AGPL — the fact only,
/// read from the names in its DLL; no code). Hooking the window class's own procedure was tried too
/// and cannot work here: that address is not inside the game's module on a live client.
///
/// A subclass must be added and removed on the thread that owns the window. Adding is refused on any
/// other thread; removing from another thread is done by the subclass itself, on a message sent to
/// the window, because the plugin's code must never be left in the window's chain after an unload.
/// </summary>
public sealed class WindowMinimumLift : IDisposable
{
    private const uint Null = 0x0000;
    private const uint WindowPosChanging = 0x0046;
    private const uint NonClientDestroy = 0x0082;
    private const uint NoSize = 0x0001;
    private const uint AbortIfHung = 0x0002;
    private static readonly nuint SubclassId = 0x4348524E; // "CHRN"

    /// <summary>The smallest outer window the lift allows. XASlave stops at a 250x200 picture.</summary>
    public const int FloorWidth = 320;
    public const int FloorHeight = 240;

    /// <summary>Windows' WINDOWPOS: the move or resize a window is about to make.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nuint wParam, nint lParam,
        uint flags, uint timeoutMs, out nint result);

    private readonly IPluginLog _log;
    private readonly SubclassProcedure _procedure; // one instance: Windows identifies the subclass by it
    private nint _window;
    private bool _tried;
    private volatile bool _installed;
    private volatile bool _removing;
    private volatile bool _lift;
    private volatile int _askedWidth;
    private volatile int _askedHeight;
    private volatile int _answeredWidth;
    private volatile int _answeredHeight;
    private volatile int _gameWidth;
    private volatile int _gameHeight;

    public WindowMinimumLift(IPluginLog log)
    {
        _log = log;
        _procedure = Procedure;
    }

    /// <summary>The subclass is in place.</summary>
    public bool Available => _installed;

    /// <summary>Why the subclass is not in place, for the status line.</summary>
    public string Reason { get; private set; } = "not tried yet";

    /// <summary>Put back a size the game raised. Off = the game's own limit stands.</summary>
    public bool Lift
    {
        get => _lift;
        set => _lift = value;
    }

    /// <summary>The outer size the game last raised a smaller request to — its own minimum — or
    /// (0, 0) if it has not raised one since the subclass went in.</summary>
    public (int Width, int Height) GameMinimum => (_gameWidth, _gameHeight);

    /// <summary>What the window said during the last resize, for the log.</summary>
    public string Evidence =>
        $"last resize asked {_askedWidth}x{_askedHeight}, the window answered {_answeredWidth}x{_answeredHeight}";

    /// <summary>Adds the subclass the first time the game window is known. Safe to call every tick.</summary>
    public bool Ensure(nint window)
    {
        if (_tried || window == 0)
            return Available;
        _tried = true;

        try
        {
            if (GetWindowThreadProcessId(window, out _) != GetCurrentThreadId())
            {
                Reason = "the game window belongs to another thread";
            }
            else if (!SetWindowSubclass(window, _procedure, SubclassId, 0))
            {
                Reason = "Windows refused the subclass";
            }
            else
            {
                _window = window;
                _installed = true;
                Reason = string.Empty;
                _log.Info("Window dock: minimum-size subclass ready");
                return true;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Reason = "window subclassing is not available";
        }

        _log.Warning($"Window dock: {Reason} - the game's minimum window size stays");
        return false;
    }

    private nint Procedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        // Leaving: asked to from another thread, or the window itself is going away.
        if (_removing || message == NonClientDestroy)
        {
            RemoveWindowSubclass(window, _procedure, SubclassId);
            _installed = false;
            return DefSubclassProc(window, message, wParam, lParam);
        }

        return message == WindowPosChanging && lParam != 0
            ? Resizing(window, wParam, lParam)
            : DefSubclassProc(window, message, wParam, lParam);
    }

    /// <summary>A move or resize about to happen. The game raises the size to its own minimum here;
    /// with the lift on, a size it raised is put back to the one asked for.</summary>
    private unsafe nint Resizing(nint window, nuint wParam, nint lParam)
    {
        var position = (WindowPos*)lParam;
        var sized = (position->Flags & NoSize) == 0;
        var width = position->Width;
        var height = position->Height;
        var result = DefSubclassProc(window, WindowPosChanging, wParam, lParam);
        if (!sized)
            return result;

        _askedWidth = width;
        _askedHeight = height;
        _answeredWidth = position->Width;
        _answeredHeight = position->Height;
        if (position->Width > width)
            _gameWidth = position->Width;
        if (position->Height > height)
            _gameHeight = position->Height;

        if (_lift)
        {
            if (position->Width > width)
                position->Width = Math.Max(width, FloorWidth);
            if (position->Height > height)
                position->Height = Math.Max(height, FloorHeight);
        }

        return result;
    }

    public void Dispose()
    {
        _lift = false;
        if (!_installed)
            return;

        if (GetWindowThreadProcessId(_window, out _) == GetCurrentThreadId())
        {
            RemoveWindowSubclass(_window, _procedure, SubclassId);
            _installed = false;
            return;
        }

        // Another thread: the subclass takes itself out on the next message, which is sent now and
        // answered before this returns.
        _removing = true;
        SendMessageTimeoutW(_window, Null, 0, 0, AbortIfHung, 2000, out _);
        if (_installed)
            _log.Error("Window dock: the minimum-size subclass could not be removed");
    }
}
