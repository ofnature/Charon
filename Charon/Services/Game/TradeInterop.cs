using System;
using Dalamud.Plugin.Services;
using Charon.Features.AutoAccept;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Mirrors a trusted LAN toon's trade actions: when they click Trade, we click Trade; when the
/// "Complete trade?" prompt appears, we answer Yes. Thin unsafe adapter, framework thread only.
///
/// Verified in-game against the live addon (node dump, 2026-07):
/// - <c>Trade</c> window, the two bottom buttons are callback 0 = Trade, 1 = Cancel. We only
///   ever fire 0 — Charon never cancels a trade.
/// - Text node 17 holds the PARTNER's character name — that is the trust gate, and it is a
///   name rather than localized prose, so it is safe to read directly.
/// - Trade phases come from <c>InventoryManager.TradeLocalState/TradeRemoteState</c>, not from
///   window text.
/// Every action is verified against the phase afterwards; a click that doesn't advance the
/// state is not retried forever.
/// </summary>
public sealed unsafe class TradeInterop
{
    private const string TradeAddon = "Trade";
    private const string ConfirmAddon = "SelectYesno";
    private const uint PartnerNameNodeId = 17;
    private const int TradeCallback = 0; // 1 = Cancel — never fired
    private const int YesCallback = 0;
    private const int MaxAttemptsPerPhase = 3;

    private readonly IGameGui _gameGui;
    private readonly Func<string, bool> _isTrusted;
    private readonly Func<bool> _enabled;
    private readonly IPluginLog _log;

    private DateTime _lastActionUtc = DateTime.MinValue;
    private TradeAction _lastAction = TradeAction.None;
    private int _attempts;

    public TradeInterop(IGameGui gameGui, Func<bool> enabled, Func<string, bool> isTrusted, IPluginLog log)
    {
        _gameGui = gameGui;
        _enabled = enabled;
        _isTrusted = isTrusted;
        _log = log;
    }

    public string Status { get; private set; } = "idle";

    /// <summary>Partner of the open trade ("" when not trading).</summary>
    public string PartnerName { get; private set; } = string.Empty;

    /// <summary>Drive the trade mirror. Call every framework tick.</summary>
    public void Update(DateTime nowUtc)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
                return;

            var local = MapPhase(inventory->TradeLocalState);
            var remote = MapPhase(inventory->TradeRemoteState);

            if (local == TradePhase.NotTrading && remote == TradePhase.NotTrading)
            {
                Reset();
                return;
            }

            PartnerName = ReadPartnerName();
            var trusted = PartnerName.Length > 0 && _isTrusted(PartnerName);
            var confirmOpen = IsAddonVisible(ConfirmAddon);

            var action = TradePolicy.Decide(_enabled(), trusted, local, remote, confirmOpen);
            if (action == TradeAction.None)
            {
                Status = PartnerName.Length == 0
                    ? "trading (partner unknown — manual)"
                    : trusted ? $"trading with {PartnerName}" : $"trading with {PartnerName} — not a LAN toon, manual";
                return;
            }

            // A fresh action resets the attempt budget; the same one is retried a few times only.
            if (action != _lastAction)
            {
                _lastAction = action;
                _attempts = 0;
            }

            if (_attempts >= MaxAttemptsPerPhase)
            {
                Status = $"{action} did not take — leaving it to you";
                return;
            }

            if (nowUtc - _lastActionUtc < TimeSpan.FromSeconds(1.0))
                return; // let the previous click land

            _lastActionUtc = nowUtc;
            _attempts++;

            var ok = action == TradeAction.LockIn
                ? FireCallback(TradeAddon, TradeCallback)
                : FireCallback(ConfirmAddon, YesCallback);

            Status = ok
                ? $"{(action == TradeAction.LockIn ? "clicked Trade" : "confirmed")} with {PartnerName}"
                : $"{action} click failed";
            _log.Info("Trade mirror: {0}", Status);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Trade mirror failed");
        }
    }

    private void Reset()
    {
        if (PartnerName.Length > 0 || _lastAction != TradeAction.None)
            Status = "idle";
        PartnerName = string.Empty;
        _lastAction = TradeAction.None;
        _attempts = 0;
    }

    /// <summary>Partner name from the trade window's name node (node 17 — verified in-game).</summary>
    private string ReadPartnerName()
    {
        try
        {
            var addon = _gameGui.GetAddonByName(TradeAddon);
            if (addon.IsNull)
                return string.Empty;

            var unit = (AtkUnitBase*)addon.Address;
            var node = unit->GetTextNodeById(PartnerNameNodeId);
            return node == null ? string.Empty : node->NodeText.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private bool IsAddonVisible(string name)
    {
        try
        {
            var addon = _gameGui.GetAddonByName(name);
            return !addon.IsNull && addon.IsVisible;
        }
        catch
        {
            return false;
        }
    }

    private bool FireCallback(string addonName, int value)
    {
        try
        {
            var addon = _gameGui.GetAddonByName(addonName);
            if (addon.IsNull || !addon.IsVisible)
                return false;

            ((AtkUnitBase*)addon.Address)->FireCallbackInt(value);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Trade callback {0} on {1} failed", value, addonName);
            return false;
        }
    }

    private static TradePhase MapPhase(TradeState state) => state switch
    {
        TradeState.TradeRequestPending => TradePhase.RequestPending,
        TradeState.SelectingTradeGoods => TradePhase.SelectingGoods,
        TradeState.LockedIn => TradePhase.LockedIn,
        TradeState.WaitingForConfirmation => TradePhase.WaitingForConfirmation,
        TradeState.Confirmed => TradePhase.Confirmed,
        _ => TradePhase.NotTrading,
    };
}
