using System;
using System.Collections.Generic;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Charon.Features.Retainers;

namespace Charon.Services.Game;

/// <summary>
/// Handles the retainer window you already have open: collects a finished venture and sends the
/// retainer straight back out, or assigns a quick exploration to an idle one.
///
/// Mechanism learned from PunishXIV/AutoRetainer (BSD-3-Clause, adapted with attribution) — but
/// deliberately NOT its control model. AutoRetainer enqueues an entire round of retainers and its
/// disable path only stops new work being queued, so an in-flight round drains through every
/// retainer while the player waits; nothing in that path checks whether the player is trying to
/// take control. This runner cannot do that: it NEVER opens a bell and NEVER selects a retainer.
/// It only answers what the single next click is, in the window that is already open. Press Stop
/// and it is over on the next tick, because there is no queue to drain; walk away from the bell
/// and it ends on its own.
///
/// SELECTING A RETAINER CLOSES THE RETAINER LIST (learned in testing: the list is finalized, not
/// left open behind the retainer's menu), so "the list is gone" CANNOT mean the session ended -
/// that read disarmed the assist the instant the user picked anyone. The session is instead any
/// retainer window being on screen, plus a few seconds of grace to cover the gap between them.
///
/// VERIFIED values: menu entries come from the game's own Addon sheet (2385 view venture report,
/// 2386/2387 the two assign-venture variants) so matching is language-independent; Quick
/// Exploration lives in the summoning bell's QuestDialogueText sheet instead (row 402). Buttons are
/// the typed ones — AddonRetainerTaskAsk.AssignButton, AddonRetainerTaskResult.ReassignButton and
/// .ConfirmButton — each gated on IsEnabled and never force-enabled.
/// </summary>
public sealed unsafe class VentureRunner
{
    private const uint AddonRowQuit = 2383;
    private const uint AddonRowViewReportDated = 2384;
    private const uint AddonRowViewReport = 2385;
    private const uint AddonRowAssignIdle = 2386;
    private const uint AddonRowAssignInProgress = 2387;

    /// <summary>The bell's own menu text, not an Addon row — verified in AutoRetainer's Lang.</summary>
    private const string BellSheet = "custom/000/CmnDefRetainerCall_00010";
    private const uint BellRowQuickExploration = 402;

    /// <summary>One click per beat: the game needs time to swap windows between steps.</summary>
    private static readonly TimeSpan ActionThrottle = TimeSpan.FromMilliseconds(400);

    /// <summary>Repeats of the same click with nothing changing — then we stop and say so.</summary>
    private const int MaxRepeats = 4;

    /// <summary>
    /// How long every retainer window may be absent before the session counts as over. The gap
    /// between one closing and the next opening is a frame or two; walking away is forever.
    /// </summary>
    private static readonly TimeSpan SessionGrace = TimeSpan.FromSeconds(5);

    private readonly IGameGui _gameGui;
    private readonly IDataManager _data;
    private readonly IPluginLog _log;

    private VentureMenuText? _text;
    private DateTime _lastActionUtc = DateTime.MinValue;
    private string _lastStep = string.Empty;
    private int _repeats;
    private DateTime _lastSessionUtc = DateTime.MinValue;

    public VentureRunner(IGameGui gameGui, IDataManager data, IPluginLog log)
    {
        _gameGui = gameGui;
        _data = data;
        _log = log;
    }

    /// <summary>Armed by an explicit button press only. Never persisted, never automatic.</summary>
    public bool Armed { get; private set; }

    /// <summary>
    /// The retainer a run was aimed at, by the game's own SORTED index — the number the retainer list takes when
    /// it opens one, and the number <c>RetainerReader</c> walks with <c>GetRetainerBySortedIndex</c>. -1 means the
    /// old behaviour: whoever is put in front of the bell gets served.
    /// </summary>
    public int TargetIndex { get; private set; } = -1;

    private bool _targetOpened;
    private bool _targetSent;
    private int _selectAttempts;

    /// <summary>A send has gone out for the retainer in front of us: its cycle is collectable-complete.</summary>
    private bool _sentThisRetainer;

    /// <summary>The retainer's menu has been closed by us — the run is finished with it.</summary>
    private bool _closedThisRetainer;

    /// <summary>
    /// The venture the next retainer should be sent on, as planned by the window (0 = no plan: the runner
    /// then only ever reassigns or takes quick exploration, which is exactly what it did before).
    /// </summary>
    public uint WantedTaskId { get; private set; }

    /// <summary>Whether the bell's retainer list is on screen right now.</summary>
    public bool RetainerListOpen => IsVisible("RetainerList");

    public string Status { get; private set; } = "off";

    public void Arm()
    {
        Armed = true;
        TargetIndex = -1;
        _targetOpened = false;
        _targetSent = false;
        _sentThisRetainer = false;
        _closedThisRetainer = false;
        _selectAttempts = 0;
        _lastSessionUtc = DateTime.UtcNow;
        _repeats = 0;
        _lastStep = string.Empty;
        Status = "armed";
    }

    /// <summary>
    /// Aim a run at ONE retainer: open it at the bell, do its round trip, then stop.
    ///
    /// This is the difference between "collect all" (serve whoever is at the bell, one at a time, for as long as
    /// the player is there) and pressing Collect on a row: that retainer, its report collected, and sent out again
    /// on <paramref name="taskId"/>, then done.
    /// </summary>
    public void ArmFor(int sortedIndex, uint taskId)
    {
        Arm();
        TargetIndex = sortedIndex;
        Plan(taskId);
        Status = $"armed for retainer #{sortedIndex}";
    }

    /// <summary>
    /// Set (or clear, with 0) the venture the next served retainer should be sent on. A new plan resets the
    /// repeat guard, because "the same click twice" means something different once the intention changed.
    /// The plan is an INTENTION, not a queue: it is read on the tick that the picker is actually open and
    /// dropped by <see cref="Stop"/>.
    /// </summary>
    public void Plan(uint taskId)
    {
        WantedTaskId = taskId;
        _repeats = 0;
        _lastStep = string.Empty;
    }

    public void Stop(string reason = "stopped")
    {
        Armed = false;
        TargetIndex = -1;
        _targetOpened = false;
        _targetSent = false;
        _sentThisRetainer = false;
        _closedThisRetainer = false;
        WantedTaskId = 0; // a stopped operation leaves no queued intention behind
        _repeats = 0;
        _lastStep = string.Empty;
        Status = reason;
    }

    public void Update(DateTime nowUtc)
    {
        if (!Armed)
            return;

        try
        {
            var screen = ReadScreen(out var entries, out var reassign, out var confirm, out var assign);

            var listOpen = RetainerListOpen;
            if (screen != VentureScreen.None || listOpen)
                _lastSessionUtc = nowUtc;

            // A run aimed at one retainer: open it first, then serve it like any other.
            if (TargetIndex >= 0)
            {
                if (screen != VentureScreen.None)
                    _targetOpened = true;

                // Finished: the send has gone out and the game is back at the retainer list. Without this the run
                // would carry on down the list, which is what "Collect all" is for, not a single row's button.
                if (_targetOpened && _targetSent && _closedThisRetainer && screen == VentureScreen.None && listOpen)
                {
                    Stop("done — collected and sent out");
                    return;
                }

                if (!_targetOpened && screen == VentureScreen.None && listOpen && TrySelectTarget(nowUtc))
                    return;
            }

            if (screen == VentureScreen.None)
            {
                // Every retainer window gone for a while means the player walked off. A window
                // merely being CLOSED proves nothing: picking a retainer closes the list itself.
                if (VentureStep.SessionEnded(nowUtc - _lastSessionUtc, SessionGrace))
                {
                    Stop("done — left the bell");
                    return;
                }

                Status = listOpen
                    ? "waiting — pick a retainer"
                    : "waiting for the retainer window";
                return;
            }

            var decision = VentureStep.Decide(Armed, screen, entries, reassign, confirm, assign, Text(), WantedTaskId,
                closeWhenDone: _sentThisRetainer);
            if (decision.Action == VentureAction.None)
            {
                Status = decision.Reason;
                return;
            }

            if (nowUtc - _lastActionUtc < ActionThrottle)
                return;

            // A click that changes nothing must never repeat forever — the same honest-refusal rule
            // the FC chest runs on. Four tries and we hand control back with the reason visible.
            var step = $"{screen}:{decision.Action}:{decision.EntryIndex}";
            _repeats = step == _lastStep ? _repeats + 1 : 0;
            _lastStep = step;
            if (_repeats >= MaxRepeats)
            {
                _log.Warning("Ventures: aborting, {0} repeated with no change", step);
                Stop($"gave up: {decision.Reason} did nothing");
                return;
            }

            _lastActionUtc = nowUtc;
            if (Execute(decision))
            {
                // A send marks the retainer's cycle as done — collecting alone would leave it idle, and the run
                // would have nothing to close.
                if (decision.Action is VentureAction.Reassign or VentureAction.PickVenture or VentureAction.Assign)
                {
                    _targetSent = true;
                    _sentThisRetainer = true;
                }

                if (decision.Action == VentureAction.Quit)
                {
                    // Back to the list: the next retainer starts a fresh cycle, and a targeted run is finished.
                    _sentThisRetainer = false;
                    _closedThisRetainer = true;
                }

                Status = decision.Reason;
                _log.Debug("Ventures: {0}", decision.Reason);
            }
            else
            {
                Status = $"could not act ({decision.Reason})";
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Venture runner threw");
            Stop("threw (see log)");
        }
    }

    /// <summary>
    /// Open the retainer this run was aimed at, through the game's own retainer list callback.
    ///
    /// The call is <c>RetainerList.Select(index)</c> as ECommons implements it — four ints on the list addon with
    /// updateState TRUE: (2, index, 0, 0) — where the index is the game's SORTED index. That number is the only
    /// handle Charon has on which retainer opens, which is why the board carries it. Bounded attempts, because a
    /// list that will not take the selection must be reported rather than clicked at forever.
    /// </summary>
    private bool TrySelectTarget(DateTime nowUtc)
    {
        if (nowUtc - _lastActionUtc < ActionThrottle)
            return false;

        if (_selectAttempts >= MaxRepeats)
        {
            Stop("gave up: the retainer list did not take the selection");
            return false;
        }

        var list = (AtkUnitBase*)_gameGui.GetAddonByName("RetainerList").Address;
        if (list == null)
            return false;

        _selectAttempts++;
        _lastActionUtc = nowUtc;
        Status = $"opening retainer #{TargetIndex}";

        var values = stackalloc AtkValue[4];
        values[0].SetInt(2);
        values[1].SetInt(TargetIndex);
        values[2].SetInt(0);
        values[3].SetInt(0);
        list->FireCallback(4, values, true);
        return true;
    }

    private bool Execute(VentureDecision decision)
    {
        switch (decision.Action)
        {
            case VentureAction.Quit:
                // Same click as an entry, named for what it means: the retainer's own menu, choosing Quit.
                var retainerMenu = (AtkUnitBase*)_gameGui.GetAddonByName("SelectString").Address;
                if (retainerMenu == null)
                    return false;
                retainerMenu->FireCallbackInt(decision.EntryIndex);
                return true;

            case VentureAction.SelectEntry:
                var menu = (AtkUnitBase*)_gameGui.GetAddonByName("SelectString").Address;
                if (menu == null)
                    return false;
                menu->FireCallbackInt(decision.EntryIndex);
                return true;

            case VentureAction.Assign:
                var ask = (AddonRetainerTaskAsk*)_gameGui.GetAddonByName("RetainerTaskAsk").Address;
                return ask != null && AtkClickHelper.ClickButton(&ask->AtkUnitBase, ask->AssignButton);

            case VentureAction.Reassign:
                var result = (AddonRetainerTaskResult*)_gameGui.GetAddonByName("RetainerTaskResult").Address;
                return result != null && AtkClickHelper.ClickButton(&result->AtkUnitBase, result->ReassignButton);

            case VentureAction.PickVenture:
            {
                // VERIFIED shape (AutoRetainer): two ints on the list addon, updateState FALSE.
                var list = (AtkUnitBase*)_gameGui.GetAddonByName("RetainerTaskList").Address;
                if (list == null || WantedTaskId == 0)
                    return false;

                var values = stackalloc AtkValue[2];
                values[0].SetInt(11);
                values[1].SetInt((int)WantedTaskId);
                list->FireCallback(2, values, false);
                return true;
            }

            case VentureAction.Confirm:
                var done = (AddonRetainerTaskResult*)_gameGui.GetAddonByName("RetainerTaskResult").Address;
                return done != null && AtkClickHelper.ClickButton(&done->AtkUnitBase, done->ConfirmButton);

            default:
                return false;
        }
    }

    private VentureScreen ReadScreen(
        out List<string> entries, out bool reassign, out bool confirm, out bool assign)
    {
        entries = new List<string>();
        reassign = confirm = assign = false;

        var result = (AddonRetainerTaskResult*)_gameGui.GetAddonByName("RetainerTaskResult").Address;
        if (result != null && result->AtkUnitBase.IsVisible)
        {
            reassign = result->ReassignButton != null && result->ReassignButton->IsEnabled;
            confirm = result->ConfirmButton != null && result->ConfirmButton->IsEnabled;
            return VentureScreen.TaskResult;
        }

        var ask = (AddonRetainerTaskAsk*)_gameGui.GetAddonByName("RetainerTaskAsk").Address;
        if (ask != null && ask->AtkUnitBase.IsVisible)
        {
            assign = ask->AssignButton != null && ask->AssignButton->IsEnabled;
            return VentureScreen.TaskAsk;
        }

        // The venture picker outranks the retainer's own menu: the menu is closed while it is up.
        var list = (AtkUnitBase*)_gameGui.GetAddonByName("RetainerTaskList").Address;
        if (list != null && list->IsVisible)
            return VentureScreen.TaskList;

        var menu = (AddonSelectString*)_gameGui.GetAddonByName("SelectString").Address;
        if (menu != null && menu->AtkUnitBase.IsVisible)
        {
            var count = menu->PopupMenu.PopupMenu.EntryCount;
            for (var i = 0; i < count; i++)
            {
                var ptr = (nint)menu->PopupMenu.PopupMenu.EntryNames[i].Value;
                entries.Add(ptr == 0 ? string.Empty : MemoryHelper.ReadSeStringNullTerminated(ptr).TextValue);
            }
            return VentureScreen.Menu;
        }

        return VentureScreen.None;
    }

    private bool IsVisible(string addon)
    {
        var unit = (AtkUnitBase*)_gameGui.GetAddonByName(addon).Address;
        return unit != null && unit->IsVisible;
    }

    /// <summary>
    /// Menu texts, read once from the sheets. A row we cannot read stays EMPTY on purpose — the
    /// pure layer refuses to match on empty text, so a missing sheet means "do nothing", never
    /// "click the first entry".
    /// </summary>
    private VentureMenuText Text()
    {
        if (_text != null)
            return _text;

        var view = AddonText(AddonRowViewReport);
        var viewDated = AddonText(AddonRowViewReportDated);
        var assignIdle = AddonText(AddonRowAssignIdle);
        var inProgress = AddonText(AddonRowAssignInProgress);
        var quick = BellText(BellRowQuickExploration);
        var quit = AddonText(AddonRowQuit);

        if (view.Length == 0 || assignIdle.Length == 0)
            _log.Warning("Ventures: retainer menu text unreadable, the assist will stay idle");
        if (quick.Length == 0)
            _log.Warning("Ventures: quick exploration text unreadable, idle retainers will be skipped");

        if (quit.Length == 0)
            _log.Warning("Ventures: the menu's quit entry is unreadable, runs will leave the retainer menu open");

        _log.Debug("Ventures: menu text - report {0} / report(dated) {1} / idle {2} / in progress {3} / quick {4} / quit {5}",
            view, viewDated, assignIdle, inProgress, quick, quit);

        _text = new VentureMenuText(view, assignIdle, inProgress, quick, quit, viewDated);
        return _text;
    }

    private string AddonText(uint row)
    {
        try
        {
            var sheet = _data.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
            return sheet.TryGetRow(row, out var r) ? r.Text.ExtractText() : string.Empty;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Ventures: Addon row {0} unreadable", row);
            return string.Empty;
        }
    }

    /// <summary>
    /// "Quick Exploration." is NOT an Addon row — it is the summoning bell's own dialogue, in a
    /// custom sheet Lumina generates no class for, so it is read as a raw row. **COLUMN 1, not 0**:
    /// column 0 holds the internal key (TEXT_CMNDEFRETAINERCALL_00010_TASK_CATEGORY_FORTUNE) and
    /// column 1 holds the text the menu actually shows. Reading column 0 shipped an assist that
    /// navigated to the category list and then stalled, matching entries against a symbol name.
    /// Unreadable means EMPTY, and empty never matches, so idle retainers are simply left alone.
    /// </summary>
    private string BellText(uint row)
    {
        try
        {
            var sheet = _data.Excel.GetSheet<Lumina.Excel.RawRow>(null, BellSheet);
            return sheet.TryGetRow(row, out var r) ? r.ReadStringColumn(1).ExtractText() : string.Empty;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Ventures: quick exploration text unreadable");
            return string.Empty;
        }
    }
}
