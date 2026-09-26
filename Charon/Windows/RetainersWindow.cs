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
    private readonly Func<string> _characterName;
    private readonly Func<ulong> _contentId;

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
        Func<ulong> contentId)
        : base("Charon — Retainers##CharonRetainers")
    {
        _retainers = retainers;
        _planner = planner;
        _contents = contents;
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
            "Charon never picks the retainer: choose it at the bell and the armed pass handles the window\n"
            + "that opens. \"Assign venture. (in progress)\" is never clicked — that entry means the retainer\n"
            + "is already out, and clicking it buys a replacement venture.");
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
        ImGui.TextColored(CharonTheme.TextMuted, _planner.Status);
        ImGui.Spacing();
    }

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

        foreach (var row in filtered)
        {
            var key = Key(row.Name);
            var option = _planner.Resolve(row, key);
            var mode = _planner.Mode(key);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var open = _expanded == key;
            if (ImGui.Selectable(row.Name + "##sel" + key, open, ImGuiSelectableFlags.SpanAllColumns))
                _expanded = open ? string.Empty : key;

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
                DrawPicker(row, ventures, key, mode, option);
        }

        ImGui.EndTable();
        ImGui.Spacing();
        ImGui.TextColored(CharonTheme.TextDisabled, _runner.Status);
    }

    private void DrawToolbar(IReadOnlyList<RetainerVenture> rows)
    {
        if (_runner.Armed)
        {
            if (Buttons.Action("Stop", true, 110f, CharonTheme.AccentRose))
                _runner.Stop("stopped");

            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextDim, "one retainer at a time — pick them at the bell");
        }
        else
        {
            if (Buttons.Action("Collect all", rows.Any(Ready), 110f))
            {
                _runner.Plan(0);
                _runner.Arm();
            }

            ImGui.SameLine();
            if (Buttons.Action("Send all", rows.Count > 0, 104f))
            {
                // One retainer per pass, chosen in game; the plan follows whoever is in front of the bell.
                _runner.Plan(0);
                _runner.Arm();
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
            // Collect means collect: the runner's reassign path keeps the venture already chosen.
            _runner.Plan(0);
            _runner.Arm();
        }

        ImGui.SameLine();
        if (Buttons.Action("Send out", row.VentureId == 0, 80f))
        {
            _runner.Plan(_planner.PlanTaskId(row, key));
            _runner.Arm();
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
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        ImGui.Indent(8f * ImGuiHelpers.GlobalScale);
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
                _contents.ArmRefresh();

            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextMuted, "open each retainer at a bell — Charon never picks one for you");
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
