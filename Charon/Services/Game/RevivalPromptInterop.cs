using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Auto-accepts the revival prompt after a raise lands. Without this, Heal Watch's raise is wasted
/// on an unattended toon: the spell resolves, the prompt opens, nobody clicks it, and the bot stays
/// on the floor.
///
/// The prompt's addon name isn't documented, so it is LEARNED the same way the teleport offer is
/// (<see cref="TeleportOfferInterop"/>): the raise-pending STATUS is the trigger, and the first
/// addon opening during that window is the prompt. Its name is persisted to config.
///
/// The gate is game STATE — dead, with raise-pending status 148 — never dialog text. Text parsing
/// is language-dependent and has broken here before (the old invite-accept path). The state window
/// is also extremely narrow, which is what makes answering a yes/no dialog safe: this only ever
/// fires while the character is dead with a raise incoming.
/// </summary>
public sealed unsafe class RevivalPromptInterop : IDisposable
{
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _raisePending;
    private readonly Func<string> _getLearnedAddonName;
    private readonly Action<string> _learnAddonName;
    private readonly IPluginLog _log;
    private readonly Random _random = new();

    private DateTime? _clickAtUtc;
    private string _clickAddonName = string.Empty;

    public string Status { get; private set; } = "idle";

    public RevivalPromptInterop(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        Func<bool> enabled,
        Func<bool> raisePending,
        Func<string> getLearnedAddonName,
        Action<string> learnAddonName,
        IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _enabled = enabled;
        _raisePending = raisePending;
        _getLearnedAddonName = getLearnedAddonName;
        _learnAddonName = learnAddonName;
        _log = log;

        // Global listener — the prompt's addon name is unknown until learned.
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, OnPostSetup);
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, OnPostSetup);
    }

    /// <summary>Drive the delayed accept. Call every framework tick.</summary>
    public void Update(DateTime nowUtc)
    {
        if (_clickAtUtc == null || nowUtc < _clickAtUtc)
            return;
        _clickAtUtc = null;

        try
        {
            // Re-check the state: the raise may have expired or someone else's landed first.
            if (!_raisePending())
            {
                Status = "raise no longer pending";
                return;
            }

            var addon = _gameGui.GetAddonByName(_clickAddonName);
            if (addon.IsNull)
            {
                Status = "revival prompt closed before accept";
                return;
            }

            var unit = (AtkUnitBase*)addon.Address;
            if (!unit->IsVisible)
            {
                Status = "revival prompt hidden before accept";
                return;
            }

            unit->FireCallbackInt(0); // 0 = Yes
            Status = "revival accepted";
            _log.Info("Revival accepted via {0}", _clickAddonName);
        }
        catch (Exception ex)
        {
            Status = "accept failed (see log)";
            _log.Warning(ex, "Failed to accept the revival prompt");
        }
    }

    private void OnPostSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!_enabled() || !_raisePending())
                return; // not dead with a raise incoming — unrelated addon

            var name = args.AddonName;
            if (name.Length == 0 || name.StartsWith('_'))
                return; // system bars/overlays are never the prompt

            var known = _getLearnedAddonName();
            if (known.Length == 0)
            {
                _learnAddonName(name);
                _log.Info("Revival prompt learned: '{0}' (persisted to config)", name);
                known = name;
            }

            if (!name.Equals(known, StringComparison.Ordinal))
                return;

            // Short delay: a bot has no reason to hesitate, but instant clicks look wrong and can
            // race the dialog's own setup.
            var delay = TimeSpan.FromSeconds(0.5 + _random.NextDouble() * 1.0);
            _clickAddonName = name;
            _clickAtUtc = DateTime.UtcNow + delay;
            Status = $"revival prompt detected — accepting in {delay.TotalSeconds:F1}s";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Revival prompt inspection failed");
        }
    }
}
