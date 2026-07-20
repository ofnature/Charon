using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Auto-commences the Duty Finder pop (the "Duty Ready" window) when the party is entirely our
/// own LAN fleet — see <see cref="Charon.Features.AutoAccept.DutyAcceptPolicy"/> for the gate.
///
/// The pop is <c>ContentsFinderConfirm</c>. Commence is callback 8 (the value AutoDuty uses in
/// production); before firing we also check the addon's typed <c>CommenceButton</c> is enabled,
/// so a greyed-out button is never poked. Never clicks Withdraw — declining stays manual.
/// </summary>
public sealed unsafe class DutyPopInterop : IDisposable
{
    private const string DutyPopAddon = "ContentsFinderConfirm";
    private const int CommenceCallback = 8;

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IGameGui _gameGui;
    private readonly Func<bool> _shouldCommence;
    private readonly IPluginLog _log;
    private readonly Random _random = new();

    private DateTime? _commenceAtUtc;

    public DutyPopInterop(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        Func<bool> shouldCommence,
        IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _shouldCommence = shouldCommence;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, DutyPopAddon, OnDutyPop);
        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, DutyPopAddon, OnDutyPopClosed);
    }

    public string Status { get; private set; } = "idle";

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, DutyPopAddon, OnDutyPop);
        _addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, DutyPopAddon, OnDutyPopClosed);
    }

    /// <summary>Drive the delayed commence. Call every framework tick.</summary>
    public void Update(DateTime nowUtc)
    {
        if (_commenceAtUtc == null || nowUtc < _commenceAtUtc)
            return;
        _commenceAtUtc = null;

        try
        {
            var addon = _gameGui.GetAddonByName(DutyPopAddon);
            if (addon.IsNull || !addon.IsVisible)
            {
                Status = "pop closed before commence";
                return;
            }

            var confirm = (AddonContentsFinderConfirm*)addon.Address;
            if (confirm->CommenceButton != null && !confirm->CommenceButton->IsEnabled)
            {
                Status = "commence not available yet";
                return;
            }

            ((AtkUnitBase*)addon.Address)->FireCallbackInt(CommenceCallback);
            Status = "duty commenced";
            _log.Info("Duty pop auto-commenced (all-LAN party)");
        }
        catch (Exception ex)
        {
            Status = "commence failed (see log)";
            _log.Warning(ex, "Failed to commence the duty pop");
        }
    }

    private void OnDutyPop(AddonEvent type, AddonArgs args)
    {
        if (!_shouldCommence())
        {
            Status = "pop ignored (not an all-LAN party)";
            return;
        }

        var delay = TimeSpan.FromSeconds(0.5 + _random.NextDouble());
        _commenceAtUtc = DateTime.UtcNow + delay;
        Status = $"commencing in {delay.TotalSeconds:F1}s";
    }

    private void OnDutyPopClosed(AddonEvent type, AddonArgs args)
    {
        // Handled manually, withdrawn, or timed out — drop any pending click.
        _commenceAtUtc = null;
    }
}
