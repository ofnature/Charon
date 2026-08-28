using System;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Mashes the Active Time Maneuver (the "QTE" addon) so an unattended toon never fails one.
/// Ported from PandorasBox's ATMSolver (BSD-3-Clause, PunishXIV/PandorasBox): while the QTE
/// window is visible, post 'A' keydown/keyup pairs to the game window every 25-50ms, and park
/// the Direct Chat game option OFF for the duration (or every press would type into the chat
/// box) — restored the moment the QTE is gone.
/// </summary>
public sealed unsafe class QteSolver
{
    private const string QteAddon = "QTE";
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const nint VkA = 0x41;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private readonly IGameGui _gameGui;
    private readonly IGameConfig _gameConfig;
    private readonly Func<bool> _enabled;
    private readonly IPluginLog _log;
    private readonly Random _random = new();
    private readonly nint _gameWindow;

    private DateTime _nextPressUtc = DateTime.MinValue;
    private bool _suppressedDirectChat;

    public QteSolver(IGameGui gameGui, IGameConfig gameConfig, Func<bool> enabled, IPluginLog log)
    {
        _gameGui = gameGui;
        _gameConfig = gameConfig;
        _enabled = enabled;
        _log = log;
        _gameWindow = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
    }

    public string Status { get; private set; } = "off";

    public void Update(DateTime now)
    {
        try
        {
            if (!_enabled())
            {
                RestoreDirectChat();
                Status = "off";
                return;
            }

            var qte = _gameGui.GetAddonByName(QteAddon);
            if (qte.IsNull || !((AtkUnitBase*)qte.Address)->IsVisible)
            {
                RestoreDirectChat();
                Status = "idle — no ATM on screen";
                return;
            }

            // Direct Chat would turn every mash into chat text — park it off until the QTE ends.
            if (!_suppressedDirectChat && _gameConfig.UiControl.TryGet("DirectChat", out bool direct) && direct)
            {
                _gameConfig.UiControl.Set("DirectChat", false);
                _suppressedDirectChat = true;
            }

            if (now < _nextPressUtc)
                return;

            PostMessage(_gameWindow, WmKeyDown, VkA, 0);
            PostMessage(_gameWindow, WmKeyUp, VkA, 0);
            _nextPressUtc = now.AddMilliseconds(_random.Next(25, 50));
            Status = "mashing the ATM";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "QTE solver threw");
            Status = "threw (see log)";
        }
    }

    private void RestoreDirectChat()
    {
        if (!_suppressedDirectChat)
            return;

        _suppressedDirectChat = false;
        try
        {
            _gameConfig.UiControl.Set("DirectChat", true);
            _log.Info("QTE: Direct Chat restored");
        }
        catch
        {
            // fail-open — the user can re-tick it; better than throwing every tick
        }
    }
}
