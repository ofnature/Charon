using System;
using System.Collections.Generic;
using Charon.Features.Tweaks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Skips cutscenes on an unattended toon: raises the game's "Skip cutscene?" prompt and answers "Yes." —
/// what a player does. Decisions are <see cref="CutsceneSkipPolicy"/>'s; this reads the game and acts.
/// Off by default (TWEAKS): on a box someone is playing, it skips their story.
///
/// THE TRIGGER is ported from ECommons' <c>AutoCutsceneSkipper</c> (MIT, NightmareXIV/ECommons, adapted
/// with attribution): a hook on the game's cutscene input handler. For a cutscene the game marks
/// SKIPPABLE (a non-null pointer at +56 of the handler's object), the original runs with ONE conditional
/// branch patched to an unconditional jump (0x75 → 0xEB), so the game acts as if skip had been pressed and
/// shows its own prompt. The patch is applied only around that one call and restored straight after, and
/// fully restored on dispose. TextAdvance and AutoRetainer install the same hook; Dalamud chains them.
///
/// A KEY PRESS DID NOT WORK (user's log, 2026-10-19): posted keys never raised the prompt — and on Saar's
/// box the Esc action is on the backtick key, whose game code (136) is not its Windows code (192), so even
/// the box's own binding could not be faked. The hook needs no key and no binding.
///
/// Every window read is IsReady-guarded first: an access violation from a window mid-setup cannot be caught.
/// </summary>
public sealed unsafe class CutsceneSkipper : IDisposable
{
    /// <summary>ECommons' signatures (AutoCutsceneSkipper): the input handler, and the branch it flips.</summary>
    private const string HandleInputSig =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 40 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? 80 79 29 00";
    private const string SkipBranchSig = "75 11 BA ?? ?? ?? ?? 48 8B CF E8 ?? ?? ?? ?? 84 C0 74 4C";
    private const byte JumpAlways = 0xEB;

    /// <summary>Addon rows: "Skip cutscene?" and "Yes." (XIVAPI, English text checked; read in the client's language).</summary>
    private static readonly uint[] SkipPromptRows = [281, 5882];
    private static readonly uint[] YesRows = [3, 4472, 4621, 8195];

    private static readonly TimeSpan ConfirmEvery = TimeSpan.FromSeconds(1);
    private const int MaxNodes = 200;

    private delegate byte CutsceneHandleInputDelegate(nint handler, float delta);

    private readonly IGameGui _gameGui;
    private readonly ICondition _condition;
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly Func<bool> _enabled;
    private readonly Hook<CutsceneHandleInputDelegate>? _hook;
    private readonly nint _branch;
    private readonly byte _branchOriginal;

    private List<string>? _skipTexts;
    private List<string>? _yesTexts;
    private DateTime _lastConfirmUtc = DateTime.MinValue;
    private int _skipped;
    private bool _wasInCutscene;
    private string _lastListSeen = string.Empty;

    /// <summary>Set each tick from the policy, read by the hook: ask the game to skip right now.</summary>
    private volatile bool _wantTrigger;

    /// <summary>The hook saw a skippable cutscene since the last tick (else the cutscene cannot be skipped).</summary>
    private volatile bool _sawSkippable;

    private bool _leaseSkip;
    private DateTime _leaseUntilUtc = DateTime.MinValue;

    public CutsceneSkipper(IGameGui gameGui, ICondition condition, IDataManager data, IPluginLog log,
        IGameInteropProvider interop, ISigScanner sigScanner, Func<bool> enabled)
    {
        _gameGui = gameGui;
        _condition = condition;
        _data = data;
        _log = log;
        _enabled = enabled;

        try
        {
            if (sigScanner.TryScanText(SkipBranchSig, out var branch))
            {
                _branch = branch;
                _branchOriginal = *(byte*)branch;
                _hook = interop.HookFromSignature<CutsceneHandleInputDelegate>(HandleInputSig, HandleInputDetour);
                _hook.Enable();
                _log.Info("[Cutscene] skip hook ready (handler {0:X}, branch {1:X} = {2:X2})",
                    _hook.Address, _branch, _branchOriginal);
            }
            else
            {
                _log.Warning("[Cutscene] skip branch signature not found — cutscenes cannot be skipped (game update?)");
            }
        }
        catch (Exception ex)
        {
            _hook = null;
            _log.Warning(ex, "[Cutscene] skip hook could not be installed — cutscenes cannot be skipped (game update?)");
        }
    }

    public string Status { get; private set; } = "off";

    /// <summary>Whether the trigger is installed — false after a game update moved its signatures.</summary>
    public bool Available => _hook != null;

    /// <summary>Skipping right now: the toggle, unless a live IPC lease says otherwise (either way).</summary>
    public bool EffectivelySkipping => CutsceneSkipPolicy.Effective(_enabled(), _leaseSkip, _leaseUntilUtc, DateTime.UtcNow);

    /// <summary>
    /// Another plugin's choice (Odysseus's roleplay toggle): skip, or WATCH, for <paramref name="seconds"/>
    /// (capped; callers refresh), overriding this box's toggle while it lasts. 0 or less releases it.
    /// </summary>
    public void Lease(bool skip, int seconds)
    {
        _leaseSkip = skip;
        _leaseUntilUtc = seconds <= 0
            ? DateTime.MinValue
            : DateTime.UtcNow.AddSeconds(Math.Min(seconds, CutsceneSkipPolicy.MaxLeaseSeconds));
        _log.Debug("[Cutscene] lease: {0}", seconds <= 0 ? "released" : $"{(skip ? "skip" : "watch")} for {seconds}s");
    }

    private string LeaseNote => DateTime.UtcNow < _leaseUntilUtc
        ? $" (IPC: {(_leaseSkip ? "skip" : "watch")})"
        : "";

    public void Update(DateTime nowUtc)
    {
        try
        {
            var inCutscene = _condition[ConditionFlag.OccupiedInCutSceneEvent]
                             || _condition[ConditionFlag.WatchingCutscene]
                             || _condition[ConditionFlag.WatchingCutscene78];
            if (inCutscene && !_wasInCutscene)
                _log.Info("[Cutscene] cutscene started (event {0}, watching {1}, watching78 {2}) - skipping {3}, hook {4}",
                    _condition[ConditionFlag.OccupiedInCutSceneEvent], _condition[ConditionFlag.WatchingCutscene],
                    _condition[ConditionFlag.WatchingCutscene78], EffectivelySkipping, Available);
            _wasInCutscene = inCutscene;

            var skipping = EffectivelySkipping;
            if (!skipping || !inCutscene)
            {
                _wantTrigger = false;
                Status = CutsceneSkipPolicy.Decide(skipping, inCutscene, -1, false).Reason
                         + (Available ? "" : " · the skip hook is unavailable (game update?)")
                         + LeaseNote + (_skipped > 0 ? $" · {_skipped} skipped this session" : "");
                return;
            }

            var yesIndex = -1;
            var otherList = Visible("SelectYesno") != null;

            var menu = (AddonSelectString*)Visible("SelectString");
            if (menu != null)
            {
                var entries = EntriesOf(menu);
                var yes = CutsceneSkipPolicy.YesIndex(entries, YesTexts());
                var titled = CutsceneSkipPolicy.IsSkipPrompt(TextsOf(&menu->AtkUnitBase), SkipTexts());
                if (CutsceneSkipPolicy.LooksLikeSkipPrompt(titled, entries.Count, yes))
                    yesIndex = yes;
                else
                    otherList = true;

                // Once per distinct list: what the reader saw, so a prompt it does not recognise names itself.
                var seen = $"{titled}|{string.Join(" / ", entries)}";
                if (seen != _lastListSeen)
                {
                    _lastListSeen = seen;
                    _log.Info("[Cutscene] list during cutscene: title matched={0}, entries=[{1}] -> {2}",
                        titled, string.Join(" / ", entries), yesIndex >= 0 ? "skip prompt" : "not ours");
                }
            }

            var decision = CutsceneSkipPolicy.Decide(true, true, yesIndex, otherList);
            _wantTrigger = decision.Action == CutsceneSkipAction.Trigger;

            Status = decision.Action switch
            {
                CutsceneSkipAction.Trigger when !Available => "cannot ask to skip: the skip hook is unavailable (game update?)",
                CutsceneSkipAction.Trigger when !_sawSkippable => "this cutscene cannot be skipped (the game says so)",
                _ => decision.Reason,
            } + LeaseNote;
            _sawSkippable = false;

            if (decision.Action == CutsceneSkipAction.ConfirmYes && nowUtc - _lastConfirmUtc >= ConfirmEvery)
            {
                _lastConfirmUtc = nowUtc;
                menu->AtkUnitBase.FireCallbackInt(decision.EntryIndex);
                _skipped++;
                _log.Info("[Cutscene] skipped (prompt answered Yes, entry {0})", decision.EntryIndex);
            }
        }
        catch (Exception ex)
        {
            _wantTrigger = false;
            _log.Warning(ex, "[Cutscene] skipper threw");
            Status = "threw (see log)";
        }
    }

    /// <summary>
    /// The game's cutscene input handler. Untouched unless the tick asked to skip AND the game marks this
    /// cutscene skippable; then the original runs once with the skip branch forced, and the byte is put back
    /// before returning — whatever happens inside.
    /// </summary>
    private byte HandleInputDetour(nint handler, float delta)
    {
        var force = false;
        try
        {
            if (_condition[ConditionFlag.OccupiedInCutSceneEvent] && *(nint*)(handler + 56) != 0)
            {
                _sawSkippable = true;
                force = _wantTrigger;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Cutscene] hook threw; leaving the game to itself");
        }

        if (!force)
            return _hook!.Original(handler, delta);

        WriteBranch(JumpAlways);
        try
        {
            return _hook!.Original(handler, delta);
        }
        finally
        {
            WriteBranch(_branchOriginal);
        }
    }

    private void WriteBranch(byte value)
    {
        var old = MemoryHelper.ChangePermission(_branch, 1, MemoryProtection.ExecuteReadWrite);
        *(byte*)_branch = value;
        MemoryHelper.ChangePermission(_branch, 1, old);
    }

    public void Dispose()
    {
        _wantTrigger = false;
        _hook?.Disable();
        _hook?.Dispose();
        if (_branch != 0 && *(byte*)_branch != _branchOriginal)
            WriteBranch(_branchOriginal);
    }

    /// <summary>A window that is up AND finished setting up, or null.</summary>
    private AtkUnitBase* Visible(string name)
    {
        var unit = (AtkUnitBase*)_gameGui.GetAddonByName(name).Address;
        return unit != null && unit->IsReady && unit->IsVisible ? unit : null;
    }

    /// <summary>
    /// Every text on the window: its own nodes, one level into its components (the title lives in the
    /// window FRAME, a component), and its string values.
    /// </summary>
    private static List<string> TextsOf(AtkUnitBase* unit)
    {
        var texts = new List<string>();
        AddTexts(&unit->UldManager, texts, descend: true);

        var values = Math.Min((int)unit->AtkValuesCount, MaxNodes);
        for (var i = 0; i < values; i++)
        {
            var value = unit->AtkValues[i];
            if (value.Type is AtkValueType.String or AtkValueType.ConstString)
                texts.Add(value.String.ToString() ?? string.Empty);
        }

        return texts;
    }

    private static void AddTexts(AtkUldManager* manager, List<string> texts, bool descend)
    {
        var count = Math.Min((int)manager->NodeListCount, MaxNodes);
        for (var i = 0; i < count; i++)
        {
            var node = manager->NodeList[i];
            if (node == null)
                continue;

            var text = node->GetAsAtkTextNode();
            if (text != null)
            {
                texts.Add(Dalamud.Utility.Utf8StringExtensions.ExtractText(text->NodeText));
                continue;
            }

            // One level only: a component's own node list, never further — the same caps every walk here keeps.
            var component = descend ? node->GetAsAtkComponentNode() : null;
            if (component != null && component->Component != null)
                AddTexts(&component->Component->UldManager, texts, descend: false);
        }
    }

    private static List<string> EntriesOf(AddonSelectString* menu)
    {
        var entries = new List<string>();
        var count = menu->PopupMenu.PopupMenu.EntryCount;
        for (var i = 0; i < count; i++)
        {
            var ptr = (nint)menu->PopupMenu.PopupMenu.EntryNames[i].Value;
            entries.Add(ptr == 0 ? string.Empty : MemoryHelper.ReadSeStringNullTerminated(ptr).TextValue);
        }

        return entries;
    }

    private List<string> SkipTexts() => _skipTexts ??= AddonTexts(SkipPromptRows);

    private List<string> YesTexts() => _yesTexts ??= AddonTexts(YesRows);

    private List<string> AddonTexts(uint[] rows)
    {
        var texts = new List<string>();
        try
        {
            var sheet = _data.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
            foreach (var id in rows)
                if (sheet != null && sheet.TryGetRow(id, out var row))
                    texts.Add(row.Text.ExtractText());
        }
        catch (Exception ex)
        {
            // Unreadable text matches nothing — the prompt then is never answered, never mis-answered.
            _log.Warning(ex, "[Cutscene] Addon text unreadable");
        }

        return texts;
    }
}
