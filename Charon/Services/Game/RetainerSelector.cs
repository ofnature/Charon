using System;
using System.Collections.Generic;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Charon.Features.Diagnostics;
using Charon.Features.Retainers;

namespace Charon.Services.Game;

/// <summary>
/// Opens the retainer a running pass is waiting for, at a bell the player is ALREADY standing at.
///
/// Every retainer on a character is reachable from the same bell, so consolidating two of them is
/// select A, withdraw, quit, select B, deposit — one session, no walking. That makes SELECTING the
/// piece worth automating, and it leaves the doctrine intact: **Charon still never takes a
/// character to a bell.** With no bell window open this does nothing, forever.
///
/// One step per beat, re-decided from live state, so closing the window or switching the toggle off
/// ends it on the next tick — there is no sequence in flight to unwind.
///
/// MECHANISM FROM GatherBuddy Reborn (FFXIV-CombatReborn/GatherBuddyReborn, Apache-2.0, adapted
/// with attribution), `GatherBuddy/Crafting/RetainerTaskExecutor.cs`, read at source rather than
/// taken second-hand:
///   - select   = `Callback.Fire(RetainerList, true, 2, (uint)sortedIndex)` — values [Int 2, UInt
///                sorted], updateState true, indexed by <c>GetRetainerBySortedIndex</c>;
///   - items    = the menu's first entry, which Charon matches by Addon row 2378's STEM instead so
///                it survives a menu that reorders (the sheet text carries a placeholder count);
///   - close    = `AgentModule.GetAgentByInternalId(AgentId.Retainer)->Hide()`, the agent not the
///                window;
///   - quit     = Addon row 2383;
///   - and a `Talk` box is cleared at every transition, because retainers greet and say goodbye.
/// Still verified by DELIVERY here — did a retainer actually open? — and three failures stand
/// the feature down for the session, because a value the game ignores must never fire forever.
/// NOT PORTED: GBR's bell-walking. Charon still never takes a character to a bell.
/// </summary>
public sealed unsafe class RetainerSelector
{
    /// <summary>Addon row for the retainer menu's "Quit." entry — text-matched, so language-independent.</summary>
    private const uint AddonRowQuit = 2383;

    /// <summary>"Entrust or withdraw items." — the entry that actually opens a retainer's bags.</summary>
    private const uint AddonRowItems = 2378;

    /// <summary>The game needs a moment to swap windows; this is not a race worth winning.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(600);

    /// <summary>How long a selection has to produce an open retainer before it counts as failed.</summary>
    /// <remarks>
    /// A real selection produces the retainer's greeting in under a second (live trace: 0.9s), so
    /// 4s of nothing is conclusive. It used to be 6s with three retries, which on a character with
    /// UNPAID retainer slots meant four "You cannot summon that retainer" errors in chat.
    /// </remarks>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(4);

    private const int MaxFailures = 3;

    private readonly IGameGui _gameGui;
    private readonly IDataManager _data;
    private readonly Func<bool> _enabled;
    private readonly Func<string?> _wanted;
    private readonly Func<string?> _open;
    private readonly Func<string?> _menu;
    private readonly IPluginLog _log;

    private DateTime _lastActionUtc = DateTime.MinValue;
    private DateTime _selectedAtUtc = DateTime.MinValue;
    private string? _selecting;
    private int _selectingRow = -1;
    private int _failures;
    private readonly Dictionary<uint, string> _entryText = new();

    /// <summary>
    /// The last steps taken, with what was on screen when each was chosen. Kept in memory ON
    /// PURPOSE: this feature's first live failure happened while dalamud.log was pinned at its
    /// size cap and recording nothing, so a trace that lives only in the log is no evidence.
    /// </summary>
    private readonly StepTrace _trace = new(capacity: 16);
    private readonly List<string> _listLayout = new();

    private SelectAction _lastAction = SelectAction.None;
    private string _lastActionTarget = string.Empty;
    private int _closeAttempts;

    /// <summary>When every bell window was first seen gone after a close or quit, or null.</summary>
    private DateTime? _emptySinceUtc;


    /// <summary>
    /// True from our first selection until we have put the bell back as we found it. Tidying up is
    /// only ever done to a session WE drove — a retainer the player opened themselves is theirs.
    /// </summary>
    private bool _driving;

    /// <summary>
    /// How long everything may be gone before the bell counts as CLOSED. Quitting a retainer
    /// leaves a gap of a few frames before the list returns, and the first version of this check
    /// read that gap as the end of the session — standing down with the list right there on
    /// screen. The venture runner already learned this (<c>VentureStep.SessionEnded</c>); the
    /// same rule and the same helper apply here.
    /// </summary>
    private static readonly TimeSpan BellGoneGrace = TimeSpan.FromSeconds(4);

    public RetainerSelector(
        IGameGui gameGui,
        IDataManager data,
        Func<bool> enabled,
        Func<string?> wanted,
        Func<string?> open,
        Func<string?> atBell,
        IPluginLog log)
    {
        _gameGui = gameGui;
        _data = data;
        _enabled = enabled;
        _wanted = wanted;
        _open = open;
        _menu = atBell;
        _log = log;
    }

    public string Status { get; private set; } = "off";

    /// <summary>Newest first. What the selector saw and did, step by step.</summary>
    public StepTrace Trace => _trace;

    /// <summary>
    /// The retainer list's own value array, captured once per session. Evidence for selecting BY
    /// NAME from the rows the list actually shows: with unpaid retainer slots on the account the
    /// list's rows stop lining up with <c>GetRetainerBySortedIndex</c>, and a sorted index landed
    /// on an unpaid slot ("You cannot summon that retainer unless you resume payment"). Held in
    /// memory because dalamud.log was pinned at its cap when that happened.
    /// </summary>
    public IReadOnlyList<string> ListLayout => _listLayout;

    /// <summary>Clears a stand-down, so a Refresh really does try again.</summary>
    public void Reset()
    {
        _driving = false;
        _listLayout.Clear();
        _emptySinceUtc = null;
        _failures = 0;
        _lastAction = SelectAction.None;
        _closeAttempts = 0;
        _selecting = null;
        Status = "ready";
    }

    public void Update(DateTime nowUtc)
    {
        try
        {
            if (_failures >= MaxFailures)
                return; // already stood down; Status says why

            var open = _open();
            ConfirmSelection(open, nowUtc);

            // A retainer GREETS you when selected and says goodbye on Quit, and that Talk box sits
            // in front of every other window — GBR clears it at five separate points in the same
            // cycle. Only while this session wants a retainer and we are at the bell, so quest
            // dialogue elsewhere is never advanced by this.
            //
            // The FAREWELL after our own Quit is the case the first version missed: by then the
            // retainer has left and the list is hidden behind the dialog, so "a retainer or the list
            // is up" was false for exactly the Talk box that blocked the next retainer. The trace
            // stopped dead after QuitOpen; this is why.
            if (_enabled() && (!string.IsNullOrWhiteSpace(_wanted()) || _driving)
                && (_menu() != null || ListVisible || _lastAction == SelectAction.QuitOpen)
                && TryAdvanceTalk(nowUtc))
                return;

            // The retainer we are standing in front of only counts as a MENU while the menu is up:
            // once their bags are open the same name arrives through `open` instead.
            var menuOpen = MenuOpen;
            var menu = menuOpen ? _menu() : null;
            if (menuOpen && string.IsNullOrWhiteSpace(menu))
            {
                Status = "at a retainer menu, but cannot tell which retainer";
                Note(nowUtc, ListVisible, null, open, Status);
                return;
            }
            var listVisible = ListVisible;
            if (listVisible)
                CaptureListLayout();

            // The bell session ended while we were mid-cycle. Say WHICH step it followed, and stand
            // down rather than carry on: that name is the evidence the next fix needs.
            var nothingUp = !listVisible && !menuOpen && open == null && _menu() == null && !TalkVisible;
            if (!nothingUp)
                _emptySinceUtc = null;
            else if (_emptySinceUtc == null)
                _emptySinceUtc = nowUtc;

            // Mid-transition every window is briefly gone; only a gap that LASTS means the bell closed.
            if (nothingUp && _emptySinceUtc is { } since
                && !VentureStep.SessionEnded(nowUtc - since, BellGoneGrace))
            {
                Status = "between windows";
                Note(nowUtc, listVisible, menu, open, Status);
                return;
            }

            if (_lastAction is SelectAction.CloseItems or SelectAction.QuitOpen && nothingUp)
            {
                Record(nowUtc, listVisible, menu, open, "BELL CLOSED after " + _lastAction + "(" + _lastActionTarget + ")");
                Status = $"the bell closed after {Describe(_lastAction)} " + "—" + " stood down (see the trace)";
                _failures = MaxFailures;
                _lastAction = SelectAction.None;
                return;
            }

            var decision = RetainerSelectPolicy.Decide(_enabled(), _wanted(), open, menu, listVisible, _driving);
            if (decision.Action is SelectAction.None or SelectAction.Ready)
            {
                if (_driving && string.IsNullOrWhiteSpace(_wanted())
                    && RetainerSelectPolicy.BackAtTheList(listVisible, open, menu))
                    _driving = false; // the list is up again: the bell is back as we found it

                Status = decision.Reason;

                // A hang is invisible in a trace of actions alone — the last one just sits there. So
                // note each NEW reason for doing nothing, once, and a stall names itself.
                Note(nowUtc, listVisible, menu, open, decision.Reason);
                return;
            }


            // Never re-select while the last selection is still settling. The list stays up for a
            // moment after a row is chosen, and without this the live trace showed Select(T'sola)
            // firing twice, 0.6s apart — harmless there, but not something to leave in before
            // this drives a fetch.
            if (decision.Action == SelectAction.Select && _selecting != null)
            {
                Note(nowUtc, listVisible, menu, open, $"waiting for {_selecting} to appear");
                return;
            }

            if (nowUtc - _lastActionUtc < Beat)
                return;

            _lastActionUtc = nowUtc;
            Status = decision.Reason;
            Record(nowUtc, listVisible, menu, open, decision.Action + "(" + decision.Retainer + ")");
            _lastAction = decision.Action;
            _lastActionTarget = decision.Retainer;
            if (decision.Action != SelectAction.CloseItems)
                _closeAttempts = 0;

            switch (decision.Action)
            {
                case SelectAction.QuitOpen:
                    ClickMenuEntry(AddonRowQuit);
                    break;

                case SelectAction.OpenItems:
                    ClickMenuEntry(AddonRowItems);
                    break;

                case SelectAction.CloseItems:
                    CloseBags();
                    break;

                default:
                    Select(decision.Retainer, nowUtc);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Retainers] selector threw");
            Status = "threw (see log)";
            _failures = MaxFailures;
        }
    }

    /// <summary>
    /// Did the last selection actually open a retainer? The callback shape is unverified here, so a
    /// silent no-op is the failure to expect — and it must stand the feature down rather than
    /// repeat forever.
    /// </summary>
    private void ConfirmSelection(string? open, DateTime nowUtc)
    {
        if (_selecting == null)
            return;

        // Two different signals, and the difference is what stops a double click:
        //   ARRIVED  — her character is in front of us, so the row was RIGHT and must not time out
        //             while her greeting plays;
        //   ENGAGED  — her MENU or BAGS are up, so the selection is COMPLETE and another may follow.
        // She arrives while the list is still on screen, a beat before it closes. Clearing the
        // selection on arrival let the policy, still seeing the list, click her row a second time
        // (live trace: Select(T'sola) at 22.5 and 23.1).
        var atBell = _menu();
        if (RetainerSelectPolicy.SelectionLanded(_selecting, open, MenuOpen ? atBell : null))
        {
            _selecting = null;
            _failures = 0;
            return;
        }

        if (RetainerSelectPolicy.SelectionLanded(_selecting, open, atBell))
            return; // she is here; the greeting and her menu are on their way

        if (nowUtc - _selectedAtUtc < OpenTimeout)
            return;

        // A wrong row is not flaky, it is WRONG: the same index would be refused every time. Stand
        // down on the first failure rather than spam the game's refusal into chat.
        _failures = MaxFailures;
        _log.Warning("[Retainers] selecting {0} did not open a retainer ({1}/{2})",
            _selecting, _failures, MaxFailures);
        _selecting = null;

        Status = $"selecting {_selecting} at row {_selectingRow} did not open them " + "—" +
                 " the list's rows may not match the retainer order (unpaid slots?); stood down";
        Record(nowUtc, ListVisible, null, open, $"SELECT FAILED {_selecting} @row {_selectingRow}");
    }

    /// <summary>
    /// Fires the list's own selection callback for the SORTED index the list is showing — the order
    /// the game draws, which is not the order retainers are stored in.
    /// </summary>
    private void Select(string retainer, DateTime nowUtc)
    {
        var addon = (AtkUnitBase*)_gameGui.GetAddonByName("RetainerList").Address;
        if (addon == null || !addon->IsReady || !addon->IsVisible)
            return;

        // The row comes from the LIST'S OWN VALUES, never from the retainer array's order: a gap in
        // that array (an unpaid or empty slot) made a sorted position point one row past the
        // wanted retainer, onto a slot the game refused to summon.
        var (texts, rowCount) = ReadListTexts(addon);
        var sorted = RetainerListRows.RowOf(texts, rowCount, retainer);
        if (sorted < 0)
        {
            Status = $"{retainer} is not a row in the list (it shows: "
                     + $"{string.Join(", ", RetainerListRows.Names(texts, rowCount))})";
            Note(nowUtc, true, null, null, Status);
            return;
        }

        var values = stackalloc AtkValue[2];
        values[0].SetInt(2);
        values[1].SetUInt((uint)sorted);
        addon->FireCallback(2, values, true);

        _selecting = retainer;
        _selectingRow = sorted;
        _driving = true;
        _selectedAtUtc = nowUtc;
        _log.Debug("[Retainers] selecting {0} at sorted index {1}", retainer, sorted);
    }

    /// <summary>
    /// Clicks a retainer-menu entry by its Addon-sheet text, so it works in any client language.
    /// An unreadable row means do NOTHING rather than click whatever sits first, which on this
    /// menu is "Entrust or withdraw gil".
    /// </summary>
    private void ClickMenuEntry(uint addonRow)
    {
        var menu = (AddonSelectString*)_gameGui.GetAddonByName("SelectString").Address;
        if (menu == null || !menu->AtkUnitBase.IsVisible)
            return;

        var wanted = EntryText(addonRow);
        if (wanted.Length == 0)
            return;

        var count = menu->PopupMenu.PopupMenu.EntryCount;
        for (var i = 0; i < count; i++)
        {
            var ptr = (nint)menu->PopupMenu.PopupMenu.EntryNames[i].Value;
            if (ptr == 0)
                continue;

            if (!MenuEntryText.Matches(MemoryHelper.ReadSeStringNullTerminated(ptr).TextValue, wanted))
                continue;

            ((AtkUnitBase*)menu)->FireCallbackInt(i);
            return;
        }
    }

    /// <summary>
    /// The list's values as text (null for a non-text value), plus the row count at [0]. The caller
    /// has already checked IsReady: an access violation from a window read cannot be caught.
    /// </summary>
    private static (List<string?> Texts, int RowCount) ReadListTexts(AtkUnitBase* addon)
    {
        var texts = new List<string?>();
        var count = Math.Min((int)addon->AtkValuesCount, 256);
        var rowCount = 0;

        for (var i = 0; i < count; i++)
        {
            var value = addon->AtkValues[i];
            if (i == 0)
                rowCount = value.Type == AtkValueType.UInt ? (int)value.UInt : value.Int;

            texts.Add(value.Type is AtkValueType.String or AtkValueType.ConstString
                ? value.String.ToString()
                : null);
        }

        return (texts, rowCount);
    }

    /// <summary>
    /// Closes the open retainer's bags, which brings their menu back. The WINDOW is closed first — what
    /// the player's X does — and live traces confirm it returns to the retainer's menu
    /// (CloseItems, then menu=T'sala). <c>AgentRetainer.Hide()</c>, GBR's route, is kept only as a
    /// last resort after three tries: GBR's own code carries fallbacks for that call leaving no menu
    /// to Quit from ("SelectString not available for Quit"), so it takes more down than the bags.
    /// </summary>
    private void CloseBags()
    {
        _closeAttempts++;

        if (_closeAttempts <= 3)
        {
            foreach (var name in new[] { "InventoryRetainerLarge", "InventoryRetainer" })
            {
                var unit = (AtkUnitBase*)_gameGui.GetAddonByName(name).Address;
                if (unit == null || !unit->IsVisible)
                    continue;

                unit->Close(true);
                return;
            }
        }

        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return;

        var agent = agentModule->GetAgentByInternalId(AgentId.Retainer);
        if (agent != null && agent->IsAgentActive())
        {
            _log.Warning("[Retainers] the bags would not close as a window; hiding the agent");
            agent->Hide();
        }
    }

    /// <summary>Clicks through a visible Talk box. True when one was there to clear.</summary>
    private bool TryAdvanceTalk(DateTime nowUtc)
    {
        var talk = (AtkUnitBase*)_gameGui.GetAddonByName("Talk").Address;
        if (talk == null || !talk->IsVisible)
            return false;

        if (nowUtc - _lastActionUtc < Beat)
            return true; // it is there; just not our turn to click yet

        _lastActionUtc = nowUtc;
        AtkClickHelper.AdvanceTalk(talk);
        Status = _lastAction == SelectAction.QuitOpen
            ? "clearing the retainer's farewell"
            : "clearing the retainer's greeting";
        Record(nowUtc, ListVisible, null, null, "AdvanceTalk");
        return true;
    }

    /// <summary>Records a reason for doing nothing, once per change, so a stall names itself.</summary>
    private void Note(DateTime nowUtc, bool list, string? menu, string? bags, string reason)
    {
        var line = Context(list, menu, bags) + "-> (" + reason + ")";
        if (_trace.Note(nowUtc.ToLocalTime(), reason, line))
            _log.Debug("[Retainers] {0}", line);
    }

    private void Record(DateTime nowUtc, bool list, string? menu, string? bags, string action)
    {
        var line = Context(list, menu, bags) + "-> " + action;
        _trace.Record(nowUtc.ToLocalTime(), line);
        _log.Debug("[Retainers] {0}", line);
    }

    /// <summary>What was on screen, so every line says what the step was chosen FROM.</summary>
    private string Context(bool list, string? menu, string? bags) =>
        $"list={(list ? "up" : "-")} menu={menu ?? "-"} bags={bags ?? "-"} talk={(TalkVisible ? "up" : "-")}  ";

    private static string Describe(SelectAction action) => action switch
    {
        SelectAction.CloseItems => "closing the bags",
        SelectAction.QuitOpen => "quitting the retainer",
        _ => action.ToString(),
    };

    private bool TalkVisible
    {
        get
        {
            var talk = (AtkUnitBase*)_gameGui.GetAddonByName("Talk").Address;
            return talk != null && talk->IsVisible;
        }
    }

    private string EntryText(uint addonRow)
    {
        if (_entryText.TryGetValue(addonRow, out var cached))
            return cached;

        var text = string.Empty;
        try
        {
            var sheet = _data.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
            if (sheet.TryGetRow(addonRow, out var row))
                text = row.Text.ExtractText();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Retainers] addon row {0} unreadable", addonRow);
        }

        _entryText[addonRow] = text;
        return text;
    }

    /// <summary>Is a RETAINER's menu up? Identified by it carrying the entrust entry.</summary>
    private bool MenuOpen
    {
        get
        {
            var menu = (AddonSelectString*)_gameGui.GetAddonByName("SelectString").Address;
            if (menu == null || !menu->AtkUnitBase.IsVisible)
                return false;

            var items = EntryText(AddonRowItems);
            if (items.Length == 0)
                return false;

            var count = menu->PopupMenu.PopupMenu.EntryCount;
            for (var i = 0; i < count; i++)
            {
                var ptr = (nint)menu->PopupMenu.PopupMenu.EntryNames[i].Value;
                if (ptr != 0 && MenuEntryText.Matches(MemoryHelper.ReadSeStringNullTerminated(ptr).TextValue, items))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Reads the list's values once. GUARDED ON IsReady FIRST: an access violation from a window
    /// read cannot be caught by try/catch, and one already took the framework tick down in this repo.
    /// </summary>
    private void CaptureListLayout()
    {
        if (_listLayout.Count > 0)
            return;

        var unit = (AtkUnitBase*)_gameGui.GetAddonByName("RetainerList").Address;
        if (unit == null || !unit->IsReady || !unit->IsVisible)
            return;

        var count = Math.Min((int)unit->AtkValuesCount, 96);
        for (var i = 0; i < count; i++)
        {
            var value = unit->AtkValues[i];
            var text = value.Type switch
            {
                AtkValueType.ConstString or AtkValueType.String => "\"" + value.String.ToString() + "\"",
                AtkValueType.Int => value.Int.ToString(),
                AtkValueType.UInt => "u" + value.UInt,
                AtkValueType.Bool => value.Byte != 0 ? "true" : "false",
                _ => null,
            };

            if (text != null)
                _listLayout.Add($"[{i}] {text}");
        }

        _log.Debug("[Retainers] RetainerList layout captured: {0} value(s)", _listLayout.Count);
    }

    private bool ListVisible
    {
        get
        {
            var unit = (AtkUnitBase*)_gameGui.GetAddonByName("RetainerList").Address;
            return unit != null && unit->IsVisible;
        }
    }
}
