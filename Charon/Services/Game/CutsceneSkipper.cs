using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Charon.Features.Tweaks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Skips cutscenes on an unattended toon: Escape during a cutscene, then "Yes." on the game's "Skip
/// cutscene?" prompt — what a player does. Decisions are <see cref="CutsceneSkipPolicy"/>'s; this reads
/// the game and acts. Off by default (TWEAKS): on a box someone is playing, it skips their story.
///
/// The key goes to our OWN process window with PostMessage (the AfkGuard / QTE mechanism — background
/// safe). Escape types nothing into chat, so unlike the QTE's 'A' it needs no Direct Chat parking. The
/// prompt is recognised by the game's own text (Addon sheet), so it works in any client language.
/// Every window read is IsReady-guarded first: an access violation from a window mid-setup cannot be caught.
/// </summary>
public sealed unsafe class CutsceneSkipper
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int VkEscape = 0x1B;

    /// <summary>Addon rows: "Skip cutscene?" and "Yes." (XIVAPI, English text checked; read in the client's language).</summary>
    private static readonly uint[] SkipPromptRows = [281, 5882];
    private static readonly uint[] YesRows = [3, 4472, 4621, 8195];

    private static readonly TimeSpan ConfirmEvery = TimeSpan.FromSeconds(1);
    private const int MaxNodes = 200;

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private readonly IGameGui _gameGui;
    private readonly ICondition _condition;
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly Func<bool> _enabled;

    private List<string>? _skipTexts;
    private List<string>? _yesTexts;
    private DateTime _lastPressUtc = DateTime.MinValue;
    private DateTime _lastConfirmUtc = DateTime.MinValue;
    private int _presses;
    private int _skipped;
    private bool _wasInCutscene;

    public CutsceneSkipper(IGameGui gameGui, ICondition condition, IDataManager data, IPluginLog log, Func<bool> enabled)
    {
        _gameGui = gameGui;
        _condition = condition;
        _data = data;
        _log = log;
        _enabled = enabled;
    }

    public string Status { get; private set; } = "off";

    private bool _leaseSkip;
    private DateTime _leaseUntilUtc = DateTime.MinValue;

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

            // A new cutscene gets a fresh allowance of presses.
            if (!inCutscene && _wasInCutscene)
                _presses = 0;
            _wasInCutscene = inCutscene;

            var skipping = EffectivelySkipping;
            if (!skipping || !inCutscene)
            {
                Status = CutsceneSkipPolicy.Decide(skipping, inCutscene, false, -1, false, TimeSpan.MaxValue, 0).Reason
                         + LeaseNote + (_skipped > 0 ? $" · {_skipped} skipped this session" : "");
                return;
            }

            var talkUp = Visible("Talk") != null;
            var yesIndex = -1;
            var otherList = Visible("SelectYesno") != null;

            var menu = (AddonSelectString*)Visible("SelectString");
            if (menu != null)
            {
                if (CutsceneSkipPolicy.IsSkipPrompt(TextsOf(&menu->AtkUnitBase), SkipTexts()))
                    yesIndex = CutsceneSkipPolicy.YesIndex(EntriesOf(menu), YesTexts());
                else
                    otherList = true;
            }

            var decision = CutsceneSkipPolicy.Decide(true, true, talkUp, yesIndex, otherList,
                nowUtc - _lastPressUtc, _presses);
            Status = decision.Reason + LeaseNote;

            switch (decision.Action)
            {
                case CutsceneSkipAction.ConfirmYes when nowUtc - _lastConfirmUtc >= ConfirmEvery:
                    _lastConfirmUtc = nowUtc;
                    menu->AtkUnitBase.FireCallbackInt(decision.EntryIndex);
                    _skipped++;
                    _presses = 0;
                    _log.Info("[Cutscene] skipped (prompt answered Yes, entry {0})", decision.EntryIndex);
                    break;

                case CutsceneSkipAction.PressEscape:
                    _lastPressUtc = nowUtc;
                    _presses++;
                    var window = Process.GetCurrentProcess().MainWindowHandle;
                    PostMessage(window, WmKeyDown, VkEscape, 0);
                    PostMessage(window, WmKeyUp, VkEscape, 0);
                    _log.Debug("[Cutscene] Escape sent ({0}/{1})", _presses, CutsceneSkipPolicy.MaxPresses);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Cutscene] skipper threw");
            Status = "threw (see log)";
        }
    }

    /// <summary>A window that is up AND finished setting up, or null.</summary>
    private AtkUnitBase* Visible(string name)
    {
        var unit = (AtkUnitBase*)_gameGui.GetAddonByName(name).Address;
        return unit != null && unit->IsReady && unit->IsVisible ? unit : null;
    }

    /// <summary>Every text on the window's own node list — the prompt's title is one of them.</summary>
    private static List<string> TextsOf(AtkUnitBase* unit)
    {
        var texts = new List<string>();
        var count = Math.Min((int)unit->UldManager.NodeListCount, MaxNodes);
        for (var i = 0; i < count; i++)
        {
            var node = unit->UldManager.NodeList[i];
            if (node == null)
                continue;
            var text = node->GetAsAtkTextNode();
            if (text != null)
                texts.Add(Dalamud.Utility.Utf8StringExtensions.ExtractText(text->NodeText));
        }

        return texts;
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
            // Unreadable text matches nothing — the skipper then only presses Escape and never confirms.
            _log.Warning(ex, "[Cutscene] Addon text unreadable");
        }

        return texts;
    }
}
