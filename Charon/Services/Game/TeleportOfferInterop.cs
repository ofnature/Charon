using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Auto-accepts the party teleport offer ("Accept Teleport to X?" — Yes / Wait / No) when
/// Follow Teleport is enabled. The offer is party-only by game rule.
///
/// The dialog's addon name isn't documented in ClientStructs yet, so it is LEARNED:
/// <c>Telepo.ActiveTeleportRequest</c> flags exactly when an offer is pending, and the first
/// addon that opens during that window is the offer dialog — its name is persisted to config
/// and every later offer is clicked directly. Yes is fired after a small random delay.
/// </summary>
public sealed unsafe class TeleportOfferInterop : IDisposable
{
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly Func<bool> _enabled;
    private readonly Func<string> _getLearnedAddonName;
    private readonly Action<string> _learnAddonName;
    private readonly IPluginLog _log;
    private readonly Random _random = new();

    private DateTime? _clickAtUtc;
    private string _clickAddonName = string.Empty;

    /// <summary>Set when an offer was accepted — lets the territory-follow fallback stand down.</summary>
    public DateTime LastAcceptUtc { get; private set; } = DateTime.MinValue;

    public string Status { get; private set; } = "idle";

    public TeleportOfferInterop(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        Func<bool> enabled,
        Func<string> getLearnedAddonName,
        Action<string> learnAddonName,
        IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _enabled = enabled;
        _getLearnedAddonName = getLearnedAddonName;
        _learnAddonName = learnAddonName;
        _log = log;

        // Global listener — we don't know the dialog's addon name until it is learned.
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, OnPostSetup);
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, OnPostSetup);
    }

    /// <summary>Drive the delayed Yes click. Call every framework tick.</summary>
    public void Update(DateTime nowUtc)
    {
        if (_clickAtUtc == null || nowUtc < _clickAtUtc)
            return;
        _clickAtUtc = null;

        try
        {
            var addon = _gameGui.GetAddonByName(_clickAddonName);
            if (addon.IsNull)
            {
                Status = "offer dialog closed before accept";
                return;
            }

            var unit = (AtkUnitBase*)addon.Address;
            if (!unit->IsVisible)
            {
                Status = "offer dialog hidden before accept";
                return;
            }

            unit->FireCallbackInt(0); // 0 = Yes
            LastAcceptUtc = nowUtc;
            Status = "teleport offer accepted";
            _log.Info("Teleport offer accepted via {0}", _clickAddonName);
        }
        catch (Exception ex)
        {
            Status = "accept failed (see log)";
            _log.Warning(ex, "Failed to accept the teleport offer");
        }
    }

    private void OnPostSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!_enabled())
                return;

            var telepo = Telepo.Instance();
            if (telepo == null || !telepo->ActiveTeleportRequest)
                return; // no offer pending — unrelated addon

            var name = args.AddonName;
            if (name.Length == 0 || name.StartsWith('_'))
                return; // system bars/overlays are never the offer dialog

            var known = _getLearnedAddonName();
            if (known.Length == 0)
            {
                // First addon to open while an offer is pending — learn it.
                _learnAddonName(name);
                _log.Info("Teleport offer dialog learned: '{0}' (persisted to config)", name);
                known = name;
            }

            if (!name.Equals(known, StringComparison.Ordinal))
                return;

            var delay = TimeSpan.FromSeconds(0.4 + _random.NextDouble() * 0.6);
            _clickAddonName = name;
            _clickAtUtc = DateTime.UtcNow + delay;
            Status = $"offer detected — accepting in {delay.TotalSeconds:F1}s";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Teleport offer inspection failed");
        }
    }
}
