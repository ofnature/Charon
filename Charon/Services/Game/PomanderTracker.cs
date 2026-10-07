using System;
using Charon.Features.DeepDungeon;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;

namespace Charon.Services.Game;

/// <summary>
/// Records every pomander the party uses in a deep dungeon, from the game's TYPED log message (row
/// <see cref="PomanderLog.UseLogMessageId"/>) rather than chat text: the message carries the user as
/// its source entity and the pomander as a DeepDungeonItem row number, so nothing depends on the
/// client's language. The client already shows party members' uses, so no relay is needed.
///
/// Which parameter slot holds the item is not documented; the template names it lnum2. Rather than
/// assume an index, the item is the one integer parameter that is a valid DeepDungeonItem row (the
/// user's parameter is an object id, far outside 1-38), and the first use of each session logs the
/// raw parameters so the read can be checked against the real thing.
/// </summary>
public sealed class PomanderTracker : IDisposable
{
    private readonly IChatGui _chat;
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly DeepDungeonReader _reader;
    private readonly PomanderLog _uses = new();
    private readonly FloorClock _clock = new();
    private bool _wasActive;
    private bool _loggedRaw;

    public PomanderTracker(IChatGui chat, IDataManager data, IPluginLog log, DeepDungeonReader reader)
    {
        _chat = chat;
        _data = data;
        _log = log;
        _reader = reader;
        _chat.LogMessage += OnLogMessage;
    }

    public PomanderLog Uses => _uses;

    /// <summary>Time per floor this run (it lives here because a floor change is what this class watches).</summary>
    public FloorClock Clock => _clock;

    public string Status { get; private set; } = "not in a deep dungeon";

    public void Update()
    {
        var snapshot = _reader.GetSnapshot();
        if (!snapshot.Active)
        {
            // Leaving ends the run; the next one numbers its floors from the start again.
            if (_wasActive)
            {
                _uses.Clear();
                _clock.Clear();
            }
            _wasActive = false;
            Status = "not in a deep dungeon";
            return;
        }

        _wasActive = true;
        _clock.Track(snapshot.Floor, DateTime.UtcNow);
        var here = PomanderLog.Describe(_uses.ActiveOn(snapshot.Floor));
        var next = PomanderLog.Describe(_uses.Pending(snapshot.Floor));
        Status = $"{_uses.Uses.Count} used this run · used here: {(here.Length > 0 ? here : "none")}"
                 + (next.Length > 0 ? $" · used for next floor: {next}" : "");
    }

    public void Dispose() => _chat.LogMessage -= OnLogMessage;

    private void OnLogMessage(ILogMessage message)
    {
        if (message.LogMessageId != PomanderLog.UseLogMessageId)
            return;

        try
        {
            var snapshot = _reader.GetSnapshot();
            if (!snapshot.Active)
                return;

            if (!_loggedRaw)
            {
                _loggedRaw = true;
                _log.Info("Pomander use, raw message: {0}", message.FormatLogMessageForDebugging());
            }

            uint itemId = 0;
            for (var i = 0; i < message.ParameterCount; i++)
            {
                if (message.TryGetIntParameter(i, out var value) && PomanderLog.IsPomander(value))
                {
                    itemId = (uint)value;
                    break;
                }
            }

            if (itemId == 0)
            {
                _log.Warning("Pomander use with no recognisable item: {0}", message.FormatLogMessageForDebugging());
                return;
            }

            var user = message.SourceEntity?.Name.ExtractText();
            _uses.Record(new PomanderUse(snapshot.Floor, itemId, ItemName(itemId),
                string.IsNullOrWhiteSpace(user) ? "someone" : user, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            // A log hook must never throw into the game's message path.
            _log.Warning(ex, "Pomander tracker: could not read a use message");
        }
    }

    /// <summary>The name the game itself prints (DeepDungeonItem's singular, column 1 in the template).</summary>
    private string ItemName(uint itemId)
    {
        var row = _data.GetExcelSheet<Lumina.Excel.Sheets.DeepDungeonItem>()?.GetRowOrDefault(itemId);
        var name = row?.Singular.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? $"pomander #{itemId}" : name;
    }
}
