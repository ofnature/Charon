using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Charon.Features.Retainers;
using Charon.Services.Game;
using Charon.Windows.Components;

namespace Charon.Windows;

/// <summary>
/// The retainer board: one row per retainer with the actions that matter, the venture each one will run
/// next, and — expanding a row — every venture it could run, ranked by what that venture pays. The second
/// tab turns the same data around: list the items you want and Charon works out who brings them back.
///
/// This is the replacement for AutoRetainer's Retainers tab, minus the parts of that window which only
/// exist for a client that logs between characters (relog buttons, character enable flags, ordering
/// locks): this client IS the character, so those controls have nothing to act on.
///
/// Nothing here starts itself. A retainer is served because a button was pressed, and the runner it hands
/// work to answers "what is the single next click in the window already open" — one click per tick, one
/// retainer at a time, with Stop always reachable.
/// </summary>
public sealed class RetainersWindow : Window
{
    private readonly RetainerReader _retainers;
    private readonly RetainerPlanner _planner;
    private readonly RetainerContentsReader _contents;
    private readonly VentureRunner _runner;
    private readonly Func<bool> _autoSelect;
    private readonly Func<string> _selectorStatus;
    private readonly Func<string> _characterName;
    private readonly Func<ulong> _contentId;
    private readonly RetainerGearService _gear;

    private int _tab;
    private string _search = string.Empty;
    private string _farmInput = string.Empty;
    private string _contentsFilter = string.Empty;
    private string _expanded = string.Empty;

    public RetainersWindow(
        RetainerReader retainers,
        RetainerPlanner planner,
        RetainerContentsReader contents,
        VentureRunner runner,
        Func<string> characterName,
        Func<ulong> contentId,
        Func<bool> autoSelect,
        Func<string> selectorStatus,
        RetainerGearService gear)
        : base("Charon — Retainers##CharonRetainers")
    {
        _gear = gear;
        BgAlpha = CharonTheme.PanelAlpha;
        _retainers = retainers;
        _planner = planner;
        _contents = contents;
        _autoSelect = autoSelect;
        _selectorStatus = selectorStatus;
        _runner = runner;
        _characterName = characterName;
        _contentId = contentId;

        Size = new Vector2(780, 430);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 260),
            MaximumSize = new Vector2(1280, 900),
        };
    }

    public override void Draw()
    {
        var rows = _retainers.Read(DateTime.UtcNow);
        var ventures = _planner.Ventures;

        DrawHeader(rows);
        DrawTabs();

        switch (_tab)
        {
            case 0: DrawBoard(rows, ventures); break;
            case 1: DrawFarm(rows, ventures); break;
            case 3: DrawGear(rows); break;
            default: DrawContents(rows); break;
        }
    }

    private void DrawHeader(IReadOnlyList<RetainerVenture> rows)
    {
        ImGui.TextColored(CharonTheme.TextStrong,
            _characterName().Length > 0 ? _characterName() : "this character");
        ImGui.SameLine();
        ImGui.TextColored(CharonTheme.TextDim,
            $"· {rows.Count} retainers · {rows.Count(r => r.VentureId != 0)} out · " +
            $"{rows.Count(Ready)} ready");
        ImGui.SameLine();
        CharonTheme.HelpMarker(
            "These buttons work at a summoning bell with its retainer list open — Charon never walks you to one.\n"
            + "A row's Collect or Send opens THAT retainer, by name; Collect all / Send all take each due retainer\n"
            + "in turn. Nothing is queued: Stop ends it on the next step.\n"
            + "\"Assign venture. (in progress)\" is never clicked — that entry means the retainer is already out,\n"
            + "and clicking it buys a replacement venture.");
    }

    private void DrawTabs()
    {
        ImGui.Spacing();
        if (ImGui.Button(_tab == 0 ? "[ Retainers ]" : "Retainers##tab"))
            _tab = 0;

        ImGui.SameLine();
        if (ImGui.Button(_tab == 1 ? "[ Farm ]" : "Farm##tab"))
            _tab = 1;

        ImGui.SameLine();
        if (ImGui.Button(_tab == 2 ? "[ Contents ]" : "Contents##tab"))
            _tab = 2;

        ImGui.SameLine();
        if (ImGui.Button(_tab == 3 ? "[ Gear ]" : "Gear##tab"))
            _tab = 3;

        ImGui.SameLine();
        ImGui.TextColored(CharonTheme.TextMuted, _planner.Status);
        ImGui.Spacing();
    }

    // ------------------------------------------------------------------- gear ---

    /// <summary>
    /// The gear optimiser: each retainer's numbers, and — for the one at the bell — the best set from
    /// gear the player holds, with a button that puts it on.
    /// </summary>
    private void DrawGear(IReadOnlyList<RetainerVenture> rows)
    {
        var now = DateTime.UtcNow;
        var view = _gear.Current(now);

        ImGui.TextColored(CharonTheme.TextDim, "Best gear for each retainer, from what you are holding.");
        ImGui.SameLine();
        CharonTheme.HelpMarker(
            "A gatherer needs enough Gathering to be sent on a venture at all, then tiers on Perception:\n"
            + "each gate cleared brings back more per run. A combat retainer does both on item level.\n\n"
            + "Gear is chosen for the venture you PICKED for her, otherwise the highest-level hunt she can take.\n"
            + "Only gear in your bags and armoury is offered, and never anything one of your own gearsets uses.\n"
            + "Melded materia is counted. A swap is only proposed when it unlocks the venture or reaches a new\n"
            + "tier — Perception between two gates buys nothing.\n\n"
            + "A retainer's gear can only be read while she is summoned, so open her at a bell to plan.");
        ImGui.Spacing();

        if (rows.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextDisabled,
                _retainers.Loaded ? "No retainers on this character." : $"Waiting for the game: {_retainers.Status}");
            return;
        }

        foreach (var row in rows)
        {
            var job = _planner.Profile(row).Job;
            var gatherer = job is "MIN" or "BTN" or "FSH";
            var atBell = view != null && view.Retainer == row.Name;

            ImGui.TextColored(atBell ? CharonTheme.TextStrong : CharonTheme.TextSecondary, row.Name);
            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextDim, $"{job} {row.Level}");
            ImGui.SameLine();

            var snapshot = _gear.Snapshot(row.Name);
            if (snapshot == null)
            {
                ImGui.TextColored(CharonTheme.TextMuted, "· gear not read yet — open her at a bell");
            }
            else
            {
                var numbers = gatherer
                    ? $"Gathering {snapshot.Gathering:N0} · Perception {snapshot.Perception:N0}"
                    : $"item level {snapshot.ItemLevel}";
                ImGui.TextColored(CharonTheme.TextDim, $"· {numbers} · read {Ago(now - snapshot.CapturedUtc)}");
            }

            if (atBell)
            {
                DrawGearPlan(view!);
            }
            else if (_gear.RetainerAtBell == row.Name)
            {
                // She is summoned but her worn gear is not in memory yet — it loads with her gear window.
                ImGui.Indent();
                if (Buttons.Action("Read her gear##" + row.Name, !_gear.Busy, 130f))
                    _gear.RequestGearWindow();
                ImGui.SameLine();
                ImGui.TextColored(CharonTheme.TextMuted, "opens \"View retainer attributes and gear\" from her menu");
                ImGui.Unindent();
            }

            ImGui.Spacing();
        }

        ImGui.TextColored(CharonTheme.TextMuted, $"gear: {_gear.Status}");
        TracePanel.Draw("Gear steps", _gear.Trace);
    }

    private void DrawGearPlan(RetainerGearService.View view)
    {
        var goal = view.Goal;
        var plan = view.Plan;
        var label = goal.Gatherer ? "Perception" : "item level";

        ImGui.Indent();
        ImGui.TextColored(CharonTheme.TextDim, $"For {goal.VentureName}:");
        ImGui.SameLine();
        if (goal.Gatherer)
        {
            ImGui.TextColored(plan.MeetsBefore ? CharonTheme.StatusGreen : CharonTheme.StatusRed,
                plan.MeetsBefore
                    ? $"Gathering {plan.Before.Gathering:N0} of {goal.RequiredGathering:N0}"
                    : $"Gathering {plan.Before.Gathering:N0} — {goal.RequiredGathering - plan.Before.Gathering:N0} short of {goal.RequiredGathering:N0}");
        }
        else
        {
            ImGui.TextColored(plan.MeetsBefore ? CharonTheme.StatusGreen : CharonTheme.StatusRed,
                plan.MeetsBefore
                    ? $"item level {plan.Before.ItemLevel} of {goal.RequiredItemLevel}"
                    : $"item level {plan.Before.ItemLevel} — {goal.RequiredItemLevel - plan.Before.ItemLevel} short of {goal.RequiredItemLevel}");
        }

        if (goal.QuantitySteps > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextDim,
                $"· tier {plan.TierBefore + 1} of {goal.QuantitySteps} ({label} gates: {string.Join(" / ", goal.Thresholds.Select(t => t.ToString("N0")))})");
        }

        if (plan.Swaps.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextSecondary, plan.Note);
        }
        else
        {
            if (ImGui.BeginTable("gearSwaps##" + view.Retainer, 4,
                    ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("Wearing", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Put on", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("From", ImGuiTableColumnFlags.WidthFixed, 62f);
                ImGui.TableHeadersRow();

                foreach (var swap in plan.Swaps)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(CharonTheme.TextDim, SlotName(swap.Slot));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(CharonTheme.TextDim, swap.Replacing == null ? "(empty)" : PieceText(swap.Replacing, goal.Gatherer));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(CharonTheme.TextStrong, PieceText(swap.Wear, goal.Gatherer));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(CharonTheme.TextDim, swap.Wear.Container is >= 0 and <= 3 ? "bags" : "armoury");
                }

                ImGui.EndTable();
            }

            var after = goal.Gatherer
                ? $"Gathering {plan.After.Gathering:N0} · Perception {plan.After.Perception:N0}"
                : $"item level {plan.After.ItemLevel}";
            ImGui.TextColored(CharonTheme.StatusGreen,
                $"After: {after} → tier {plan.TierAfter + 1} of {goal.QuantitySteps} · {plan.Note}");

            if (_gear.Busy)
            {
                if (Buttons.Action("Stop##gear", true, 110f, CharonTheme.AccentRose))
                    _gear.Stop();
            }
            else if (Buttons.Action($"Equip {plan.Swaps.Count} piece(s)##gear", view.CannotEquip == null, 150f))
            {
                _gear.StartEquip(DateTime.UtcNow);
            }

            ImGui.SameLine();
            if (view.CannotEquip != null)
                ImGui.TextColored(CharonTheme.StatusYellow, view.CannotEquip);
            else
                ImGui.TextColored(CharonTheme.TextMuted, "one piece at a time, each checked — the replaced gear goes back where the new piece came from");
        }

        if (view.Holding != null)
            ImGui.TextColored(CharonTheme.StatusYellow, view.Holding);
        else
            ImGui.TextColored(CharonTheme.TextMuted, $"{view.Candidates} piece(s) of yours fit her and are in no gearset");

        ImGui.Unindent();
    }

    private static string PieceText(RetainerPiece piece, bool gatherer) =>
        gatherer
            ? $"{piece.Name}{(piece.Hq ? " HQ" : "")}  (G {piece.Gathering} / P {piece.Perception})"
            : $"{piece.Name}{(piece.Hq ? " HQ" : "")}  (i{piece.ItemLevel})";

    private static string SlotName(Charon.Features.Gear.GearSlot slot) => slot switch
    {
        Charon.Features.Gear.GearSlot.MainHand => "Main hand",
        Charon.Features.Gear.GearSlot.OffHand => "Off hand",
        Charon.Features.Gear.GearSlot.RingRight => "Ring (R)",
        Charon.Features.Gear.GearSlot.RingLeft => "Ring (L)",
        _ => slot.ToString(),
    };

    private static string Ago(TimeSpan age) =>
        age.TotalMinutes < 1 ? "just now"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
        : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
        : $"{(int)age.TotalDays}d ago";

    // ------------------------------------------------------------------ board ---

    private void DrawBoard(IReadOnlyList<RetainerVenture> rows, IReadOnlyList<VentureDef> ventures)
    {
        DrawToolbar(rows);

        if (rows.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextDisabled,
                _retainers.Loaded ? "No retainers on this character." : $"Waiting for the game: {_retainers.Status}");
            return;
        }

        var filtered = rows
            .Where(r => _search.Length == 0 || r.Name.Contains(_search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!ImGui.BeginTable("retainerRows", 6,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 104f);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 66f);
        ImGui.TableSetupColumn("Runs next", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Out", ImGuiTableColumnFlags.WidthFixed, 62f);
        ImGui.TableSetupColumn("Bag", ImGuiTableColumnFlags.WidthFixed, 34f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 214f);
        ImGui.TableHeadersRow();

        RetainerVenture? expanded = null;

        foreach (var row in filtered)
        {
            var key = Key(row.Name);
            var option = _planner.Resolve(row, key);
            var mode = _planner.Mode(key);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var open = _expanded == key;
            // NOT SpanAllColumns: that stretched the selectable's rect over the Actions column as well, and ImGui
            // keeps the hover locked to the item that claimed it — so only the bottom sliver of a button that
            // stuck out below the row's text line could be clicked. The name is the toggle.
            if (ImGui.Selectable(row.Name + "##sel" + key, open))
                _expanded = open ? string.Empty : key;

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(open ? "Hide the venture list" : "Choose which venture this retainer runs");

            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.TextDim, _planner.Profile(row).Job.Length > 0
                ? _planner.Profile(row).Job
                : "—");
            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextSecondary, row.Level.ToString());

            ImGui.TableNextColumn();
            ImGui.TextColored(mode == VentureAssignment.Off ? CharonTheme.TextMuted : CharonTheme.AccentSoft,
                DescribePlan(mode, option));

            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.TextDim, DescribeOut(row));

            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.TextDim, row.ItemCount.ToString());

            ImGui.TableNextColumn();
            DrawRowActions(key, row, option);

            if (open)
                expanded = row;
        }

        ImGui.EndTable();

        // The picker is drawn OUTSIDE the table, at full width. It used to be drawn inside the row it belongs to,
        // in that row's first column — 104px wide — so a six-column table with ~500px of fixed widths was squeezed
        // into one cell and every column collapsed: the headers rendered as "Ven…", cells as single characters,
        // and the whole thing was unreadable. A table belongs somewhere it fits.
        if (expanded is { } pickedRow)
        {
            var pickedKey = Key(pickedRow.Name);
            ImGui.Spacing();
            ImGui.Separator();
            DrawPicker(pickedRow, ventures, pickedKey, _planner.Mode(pickedKey),
                _planner.Resolve(pickedRow, pickedKey));
        }

        ImGui.Spacing();
        ImGui.TextColored(CharonTheme.TextDisabled, $"runner: {_runner.Status}");
    }

    private void DrawToolbar(IReadOnlyList<RetainerVenture> rows)
    {
        if (_runner.Armed)
        {
            if (Buttons.Action("Stop", true, 110f, CharonTheme.AccentRose))
                _runner.Stop("stopped");

            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextDim, _runner.RoundActive
                ? "working through your retainers, one at a time"
                : "one retainer at a time");
        }
        else
        {
            // Both run a ROUND at the bell: each due retainer is opened by name, collected and sent back out.
            // "Collect all" takes only the finished ones; "Send all" also sends anyone sitting idle.
            if (Buttons.Action("Collect all", rows.Any(Ready), 110f))
            {
                _runner.ArmRound(served => _planner.NextInRound(
                    _retainers.Read(DateTime.UtcNow), served, _contentId(), includeIdle: false, DateTime.UtcNow));
            }

            ImGui.SameLine();
            if (Buttons.Action("Send all", rows.Count > 0, 104f))
            {
                _runner.ArmRound(served => _planner.NextInRound(
                    _retainers.Read(DateTime.UtcNow), served, _contentId(), includeIdle: true, DateTime.UtcNow));
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        ImGui.InputTextWithHint("##search", "search…", ref _search, 64);
        ImGui.Spacing();
    }

    private void DrawRowActions(string key, RetainerVenture row, VentureOption? option)
    {
        var ready = Ready(row);

        if (Buttons.Action(ready ? "Collect" : "Collect##idle", ready, 76f))
        {
            // Collect THIS retainer: open it at the bell by name, collect the report, then send it out
            // again on the venture the planner has for it. One press, one retainer's whole round trip.
            _runner.ArmFor(row.Name, _planner.PlanTaskId(row, key));
        }

        ImGui.SameLine();
        if (Buttons.Action("Send out", row.VentureId == 0, 80f))
        {
            // The row names the retainer, so the press opens HER at the bell rather than waiting for a pick.
            _runner.ArmFor(row.Name, _planner.PlanTaskId(row, key));
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"plan##{key}"))
            _expanded = _expanded == key ? string.Empty : key;

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Choose which venture this retainer runs");

        ImGui.SameLine();
        if (ImGui.SmallButton($"farm##{key}"))
            _tab = 1;

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(option != null
                ? $"Per run: x{option.QuantityPerRun}{(option.TierKnown ? string.Empty : " (assumed)")}"
                : "Nothing planned for this retainer");
    }

    /// <summary>The expansion: the assignment mode, and every venture this retainer could be sent on.</summary>
    private void DrawPicker(
        RetainerVenture row,
        IReadOnlyList<VentureDef> ventures,
        string key,
        VentureAssignment mode,
        VentureOption? planned)
    {
        ImGui.TextColored(CharonTheme.TextSecondary, row.Name);
        ImGui.SameLine();
        ImGui.TextColored(CharonTheme.TextDim, "ASSIGNMENT");

        foreach (var (value, label) in Modes)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton($"{label}##{key}", mode == value))
                _planner.SetMode(key, value);
        }

        ImGui.SameLine();
        ImGui.TextColored(CharonTheme.TextMuted, mode switch
        {
            VentureAssignment.Off => "nothing is assigned automatically",
            VentureAssignment.Picked when planned != null => $"planned: {planned.Venture.Name}",
            VentureAssignment.Picked => "no venture picked yet",
            VentureAssignment.FromFarm => "only what the farm list asks for",
            _ => "the best this retainer can actually run",
        });

        if (!ImGui.BeginTable("ventureOptions##" + key, 6,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit,
                new Vector2(0, 170f)))
        {
            ImGui.Unindent(8f * ImGuiHelpers.GlobalScale);
            return;
        }

        ImGui.TableSetupColumn("Venture", ImGuiTableColumnFlags.WidthFixed, 148f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 64f);
        ImGui.TableSetupColumn("Returns", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("Exp", ImGuiTableColumnFlags.WidthFixed, 84f);
        ImGui.TableSetupColumn("Why not", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("##set", ImGuiTableColumnFlags.WidthFixed, 56f);
        ImGui.TableHeadersRow();

        foreach (var option in VentureCatalog.Rank(_planner.Profile(row), ventures, _planner.Prices()).Take(14))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            var isPlanned = mode == VentureAssignment.Picked && planned?.Venture.TaskId == option.Venture.TaskId;
            ImGui.TextColored(
                option.Runnable
                    ? isPlanned ? CharonTheme.Accent : CharonTheme.TextSecondary
                    : CharonTheme.TextMuted,
                (isPlanned ? "▸ " : string.Empty) + option.Venture.Name);

            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.TextDim, option.Venture.IsRandom ? "explore" : "hunt");

            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.TextDim,
                option.QuantityPerRun > 0
                    ? $"{option.QuantityPerRun} × {PriceText(option)}"
                    : "random pool");

            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.TextDim, $"{option.ExpPerHour:N0}/h");

            ImGui.TableNextColumn();
            ImGui.TextColored(option.Runnable ? CharonTheme.TextMuted : CharonTheme.StatusRed,
                option.Blocker ?? (option.Venture.IsRandom ? "yield is not in the sheet" : string.Empty));

            ImGui.TableNextColumn();
            if (!option.Runnable)
                ImGui.TextColored(CharonTheme.TextMuted, "—");
            else if (Buttons.Action("Set", true, 52f))
            {
                _planner.SetMode(key, VentureAssignment.Picked);
                _planner.SetPicked(key, option.Venture.TaskId);
            }
        }

        ImGui.EndTable();
        ImGui.TextColored(CharonTheme.TextMuted,
            "Quantities marked ? are assumed: the retainer's gear stats are not read yet. "
            + $"{_planner.Prices().Count(p => p.Value > 0)} of {_planner.Prices().Count} item prices known.");
        ImGui.Unindent(8f * ImGuiHelpers.GlobalScale);
    }


    // ---------------------------------------------------------------- contents ---

    /// <summary>
    /// What each retainer holds, as last seen — the store other plugins read over IPC, shown so it can be
    /// judged: every line carries how old it is, and a retainer nobody has opened is listed as UNKNOWN
    /// rather than drawn as empty, because that is the difference that matters when you are hunting materials.
    /// </summary>
    private void DrawContents(IReadOnlyList<RetainerVenture> rows)
    {
        if (_contents.Busy)
        {
            if (Buttons.Action("Stop", true, 90f, CharonTheme.AccentRose))
                _contents.Stop("stopped");

            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextDim, _contents.Status);
        }
        else
        {
            if (Buttons.Action("Refresh all", true, 110f))
                _contents.ArmRefresh(force: true);

            ImGui.SameLine();
            // With selection on, Charon DOES pick the next retainer while you stand at a bell — so
            // the hint says what it is doing rather than a rule that no longer holds.
            ImGui.TextColored(CharonTheme.TextMuted, _autoSelect()
                ? $"at the bell, Charon opens each one — {_selectorStatus()}"
                : "open each retainer at a bell — Charon never picks one for you");
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        ImGui.InputTextWithHint("##contentsSearch", "filter items…", ref _contentsFilter, 64);
        ImGui.Spacing();

        var bags = _contents.Bags();
        var now = DateTime.UtcNow;

        if (bags.Count == 0 && rows.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextDisabled, _retainers.Status);
            return;
        }

        if (bags.Count == 0)
            ImGui.TextColored(CharonTheme.TextMuted,
                "Nothing recorded yet: open a retainer at a bell and its bags appear here.");

        foreach (var row in rows)
        {
            var bag = bags.FirstOrDefault(b => b.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase));
            var key = Key(row.Name);

            if (bag == null)
            {
                ImGui.TextColored(CharonTheme.TextMuted, $"{row.Name} — unknown (never opened this session)");
                ImGui.SameLine();
                ImGui.TextColored(CharonTheme.TextDisabled, _planner.Mode(key) == VentureAssignment.Off
                    ? string.Empty
                    : string.Empty);
                continue;
            }

            var age = now - bag.CapturedUtc;
            var opens = ImGui.TreeNodeEx($"{row.Name}##contents{key}",
                ImGuiTreeNodeFlags.SpanAvailWidth,
                $"{row.Name} — {bag.Stacks.Count} stack(s), seen {Describe(age)} ago");

            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextDisabled,
                $"{bag.Stacks.Where(s => !s.Hq).Sum(s => s.Qty)} normal · {bag.Stacks.Where(s => s.Hq).Sum(s => s.Qty)} HQ");

            if (!opens)
                continue;

            var stacks = bag.Stacks
                .Where(s => _contentsFilter.Length == 0
                            || _planner.ItemName(s.ItemId).Contains(_contentsFilter, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Hq)
                .ThenByDescending(s => s.Qty)
                .ToList();

            if (stacks.Count == 0)
            {
                ImGui.TextColored(CharonTheme.TextDisabled,
                    _contentsFilter.Length == 0 ? "  (empty)" : "  nothing matching the filter");
            }
            else if (ImGui.BeginTable($"contentsTable{key}", 3,
                         ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableHeadersRow();

                foreach (var stack in stacks)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    Styling.Text(_planner.ItemName(stack.ItemId), CharonTheme.TextSecondary);

                    ImGui.TableNextColumn();
                    Styling.Text(stack.Qty.ToString("N0"), CharonTheme.TextDim);

                    ImGui.TableNextColumn();
                    Styling.Text(stack.Hq ? "HQ" : "normal", stack.Hq ? CharonTheme.AccentAmber : CharonTheme.TextMuted);
                }

                ImGui.EndTable();
            }

            ImGui.TreePop();
        }

        ImGui.Spacing();
        DrawStatus(_contents);
    }

    private static void DrawStatus(RetainerContentsReader contents)
    {
        ImGui.TextColored(CharonTheme.TextDisabled, contents.Status);
        ImGui.SameLine();
        ImGui.TextColored(CharonTheme.TextDisabled, $"· last: {contents.LastResult}");
    }

    private static string Describe(TimeSpan age) => age.TotalMinutes switch
    {
        < 2 => "just now",
        < 90 => $"{age.TotalMinutes:0} min",
        < 60 * 36 => $"{age.TotalHours:0} h",
        _ => $"{age.TotalDays:0} days",
    };

    // ------------------------------------------------------------------- farm ---

    private void DrawFarm(IReadOnlyList<RetainerVenture> rows, IReadOnlyList<VentureDef> ventures)
    {
        ImGui.SetNextItemWidth(300f);
        ImGui.InputTextWithHint("##farmAdd", "add an item… (\"Manganese Ore x500\")", ref _farmInput, 96);
        ImGui.SameLine();
        if (Buttons.Action("Add", _farmInput.Trim().Length > 0, 70f))
        {
            _planner.AddFarmTarget(_farmInput);
            _farmInput = string.Empty;
        }

        ImGui.SameLine();
        var targets = _planner.FarmTargets();
        if (Buttons.Action("Clear", targets.Count > 0, 70f, CharonTheme.TextDim))
        {
            foreach (var target in targets)
                _planner.RemoveFarmTarget(target);
        }

        ImGui.Spacing();

        if (targets.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextDisabled,
                "Nothing on the farm list. Add an item and the retainers' plans follow from it.");
            return;
        }

        var plans = _planner.PlanFarm(rows);

        if (!ImGui.BeginTable("farmRows", 7,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Wanted", ImGuiTableColumnFlags.WidthFixed, 62f);
        ImGui.TableSetupColumn("Who", ImGuiTableColumnFlags.WidthFixed, 104f);
        ImGui.TableSetupColumn("Venture", ImGuiTableColumnFlags.WidthFixed, 140f);
        ImGui.TableSetupColumn("Per run", ImGuiTableColumnFlags.WidthFixed, 64f);
        ImGui.TableSetupColumn("Runs", ImGuiTableColumnFlags.WidthFixed, 44f);
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 28f);
        ImGui.TableHeadersRow();

        foreach (var plan in plans)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(plan.Blocker == null ? CharonTheme.TextStrong : CharonTheme.TextMuted,
                plan.Target.Name.Length > 0 ? plan.Target.Name : $"item {plan.Target.ItemId}");

            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.TextDim, plan.Target.Wanted?.ToString("N0") ?? "—");

            ImGui.TableNextColumn();
            ImGui.TextColored(plan.Blocker == null ? CharonTheme.TextSecondary : CharonTheme.StatusRed,
                plan.Blocker ?? plan.Retainer);

            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.TextDim, plan.Venture?.Name ?? "—");

            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.TextDim, plan.QuantityPerRun > 0 ? $"x{plan.QuantityPerRun}" : "—");

            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.TextDim, plan.RunsNeeded?.ToString() ?? "—");

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"✕##rm{plan.Target.ItemId}"))
                _planner.RemoveFarmTarget(plan.Target);
        }

        ImGui.EndTable();

        ImGui.Spacing();
        var defaultMode = _planner.DefaultMode;
        ImGui.TextColored(CharonTheme.TextDim, "AUTO ASSIGN — default for retainers with no mode of their own");
        foreach (var (value, label) in new (VentureAssignment Mode, string Label)[]
                 {
                     (VentureAssignment.BestValue, "best it can do"),
                     (VentureAssignment.FromFarm, "farm list only"),
                     (VentureAssignment.Off, "off"),
                 })
        {
            ImGui.SameLine();
            if (ImGui.RadioButton($"{label}##defaultMode", defaultMode == value))
                _planner.SetDefaultMode(value);
        }

        ImGui.Spacing();
        ImGui.TextColored(CharonTheme.TextMuted,
            "Setup order: put items on the farm list, then send the retainers you want working on it. "
            + "Nobody is sent anywhere by this list — it decides WHAT gets chosen when a send happens.");
    }

    // ----------------------------------------------------------------- helpers ---

    private static readonly (VentureAssignment Mode, string Label)[] Modes =
    [
        (VentureAssignment.BestValue, "best it can do"),
        (VentureAssignment.Picked, "pick one"),
        (VentureAssignment.FromFarm, "farm list"),
        (VentureAssignment.Off, "off"),
    ];

    private static bool Ready(RetainerVenture row) => row.CompleteUtc is { } t && t <= DateTime.UtcNow;

    private string Key(string retainer) => _planner.Key(_contentId(), retainer);

    private string PriceText(VentureOption option) =>
        option.PriceEach > 0 ? $"{option.PriceEach:N0}g ({option.ValuePerHour:N0}/h)" : "price unknown";

    private static string DescribePlan(VentureAssignment mode, VentureOption? option) => mode switch
    {
        VentureAssignment.Off => "— off",
        VentureAssignment.FromFarm when option == null => "nothing on the farm list",
        VentureAssignment.Picked when option == null => "no venture picked",
        _ when option == null => "nothing this retainer can run",
        _ => $"{(mode == VentureAssignment.Picked ? "picked" : mode == VentureAssignment.FromFarm ? "farm" : "best")} → {option!.Venture.Name}",
    };

    private static string DescribeOut(RetainerVenture row)
    {
        if (row.CompleteUtc is not { } done)
            return row.VentureId == 0 ? "idle" : "out";

        var left = done - DateTime.UtcNow;
        return left <= TimeSpan.Zero ? "ready" : $"{left.Hours}h {left.Minutes:00}m";
    }
}
