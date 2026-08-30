using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Fills the item turn-in window ("Request" — quest hand-ins, supply missions) automatically:
/// for each empty slot, open its item picker and select the first offered item. Ported from
/// PandorasBox's AutoSelectTurnin (BSD-3-Clause, PunishXIV/PandorasBox); the callbacks are
/// theirs, production-verified: slot fill = callback (2, slot, 0, 0) on the Request addon, the
/// picker ("ContextIconMenu") select = callback (0, 0, 1021003, 0, 0), both with updateState
/// FALSE.
///
/// Confirm (the Hand Over press) is a separate opt-in — filling the window costs nothing, but
/// handing items over is a decision, so it defaults off (Pandora's default too). The press
/// replays the typed HandOverButton's own event via <see cref="AtkClickHelper"/>.
/// </summary>
public sealed unsafe class TurnInFiller
{
    private const string RequestAddon = "Request";
    private const string PickerAddon = "ContextIconMenu";
    private static readonly TimeSpan ActionPacing = TimeSpan.FromMilliseconds(150);

    private readonly IGameGui _gameGui;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _autoConfirm;
    private readonly IPluginLog _log;

    private readonly HashSet<int> _filledSlots = new();
    private DateTime _lastActionUtc = DateTime.MinValue;
    private bool _pickerPending;

    public TurnInFiller(IGameGui gameGui, Func<bool> enabled, Func<bool> autoConfirm, IPluginLog log)
    {
        _gameGui = gameGui;
        _enabled = enabled;
        _autoConfirm = autoConfirm;
        _log = log;
    }

    public string Status { get; private set; } = "idle";

    public void Update(DateTime now)
    {
        try
        {
            if (!_enabled())
            {
                Status = "off";
                return;
            }

            var request = _gameGui.GetAddonByName(RequestAddon);
            if (request.IsNull || !request.IsVisible)
            {
                if (_filledSlots.Count > 0 || _pickerPending)
                {
                    _filledSlots.Clear();
                    _pickerPending = false;
                }

                Status = "idle — no turn-in window";
                return;
            }

            if (now - _lastActionUtc < ActionPacing)
                return;

            var addon = (AddonRequest*)request.Address;

            // A picker we opened is up — select the first (and for turn-ins, only) offered item.
            var picker = _gameGui.GetAddonByName(PickerAddon);
            if (_pickerPending && !picker.IsNull && picker.IsVisible)
            {
                var values = stackalloc AtkValue[5];
                values[0].SetInt(0);
                values[1].SetInt(0);
                values[2].SetUInt(1021003);
                values[3].SetUInt(0);
                values[4].SetUInt(0);
                ((AtkUnitBase*)picker.Address)->FireCallback(5, values, false);
                _pickerPending = false;
                _lastActionUtc = now;
                Status = $"filled slot {_filledSlots.Count}";
                return;
            }

            // Fill the next empty slot.
            for (var i = 1; i <= addon->EntryCount; i++)
            {
                if (_filledSlots.Contains(i))
                    continue;

                var values = stackalloc AtkValue[4];
                values[0].SetInt(2);
                values[1].SetInt(i - 1);
                values[2].SetUInt(0);
                values[3].SetUInt(0);
                addon->AtkUnitBase.FireCallback(4, values, false);
                _filledSlots.Add(i);
                _pickerPending = true;
                _lastActionUtc = now;
                Status = $"opening picker for slot {i}";
                return;
            }

            // All slots filled — hand over only when explicitly opted in.
            if (!_autoConfirm())
            {
                Status = $"filled {addon->EntryCount} slot(s) — Hand Over is yours";
                return;
            }

            if (addon->HandOverButton != null && addon->HandOverButton->IsEnabled)
            {
                if (AtkClickHelper.ClickButton(&addon->AtkUnitBase, addon->HandOverButton))
                {
                    _lastActionUtc = now;
                    Status = "handed over";
                    _log.Info("Turn-in: handed over {0} item(s)", addon->EntryCount);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Turn-in filler threw");
            Status = "threw (see log)";
        }
    }
}
