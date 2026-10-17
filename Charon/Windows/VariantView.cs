using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.Variant;
using Charon.Services.Game;
using Charon.Windows.Components;
using Dalamud.Bindings.ImGui;

namespace Charon.Windows;

/// <summary>
/// The variant guide's body, shared by the main window's Variant section (every dungeon) and the in-dungeon
/// guide window (the dungeon you are in) so the two can never disagree about a route.
/// </summary>
public static class VariantView
{
    /// <param name="display">Turns a character name into what is shown (the scramble toggle).</param>
    public static void DrawDungeon(VariantService service, VariantDungeon dungeon, bool compact, Func<string, string> display)
    {
        var progress = service.Progress(dungeon);
        var target = service.Target(dungeon);
        var suggested = service.Suggestion(dungeon);

        if (progress == null)
            Styling.Text("Record not loaded yet — routes shown without done marks.", CharonTheme.TextMuted);
        else if (progress.Complete)
            Styling.Text($"All {dungeon.Routes.Count} records done.", CharonTheme.AccentMint);
        else
            Styling.Text($"{progress.Done.Count} of {dungeon.Routes.Count} records done · {progress.Left.Count} to go",
                CharonTheme.TextSecondary);

        DrawPicker(service, dungeon, progress, target, suggested);
        DrawWhoGains(service, dungeon, target, display);
        ImGui.Spacing();
        DrawRoute(service, dungeon, target, progress);

        if (compact)
            return;

        ImGui.Spacing();
        var group = service.TableGroup();
        DrawTable(service, dungeon, progress, target, group, display);
        ImGui.Spacing();
        DrawByPath(dungeon, group, display);
    }

    private static void DrawPicker(VariantService service, VariantDungeon dungeon, VariantProgress? progress,
        int target, int? suggested)
    {
        ImGui.SetNextItemWidth(260f);
        if (ImGui.BeginCombo($"Guide route##variant{dungeon.Series}", Label(service, dungeon, target, progress)))
        {
            foreach (var route in dungeon.Routes)
            {
                if (ImGui.Selectable(Label(service, dungeon, route.Number, progress), route.Number == target))
                    service.Choose(dungeon, route.Number);
            }

            ImGui.EndCombo();
        }

        if (service.IsChosen(dungeon))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Use suggestion##variant{dungeon.Series}"))
                service.ClearChoice(dungeon);
            if (ImGui.IsItemHovered())
                Styling.Tooltip(suggested == null ? "Nobody needs anything here." : $"Route {suggested} is next.");
        }
        else if (suggested != null)
        {
            ImGui.SameLine();
            Styling.Text(service.SuggestsForParty ? "party pick" : "next to do", CharonTheme.TextMuted);
            if (ImGui.IsItemHovered())
                Styling.Tooltip(service.SuggestsForParty
                    ? "The route that earns a record for the most of the party (one record each per\n"
                      + "run). Ties go to the secret route, then the lowest number."
                    : "The secret route first: a failed attempt carries on into one of its sibling\n"
                      + "routes and still earns a record. Then the lowest-numbered route left.");
        }
    }

    /// <summary>Who in the party earns a record from this route, and whose record is still unknown.</summary>
    private static void DrawWhoGains(VariantService service, VariantDungeon dungeon, int target, Func<string, string> display)
    {
        var party = service.Group(forceFleet: false);
        if (!party.IsParty)
            return;

        var gains = VariantNeeds.NeededBy(dungeon, target, party.Known);
        var line = gains.Count == 0
            ? "Nobody in the party gets a record from this route."
            : gains.Count == party.Known.Count
                ? $"Everyone in the party gets a record ({gains.Count})."
                : $"Records for: {Names(gains, display)} ({gains.Count} of {party.Known.Count})";
        Styling.TextWrapped(line, gains.Count == 0 ? CharonTheme.AccentAmber : CharonTheme.AccentMint);

        if (party.Unknown.Count > 0)
            Styling.TextWrapped($"No record heard from: {Names(party.Unknown, display)} — not running Charon, or not reported yet.",
                CharonTheme.TextMuted);
    }

    private static void DrawRoute(VariantService service, VariantDungeon dungeon, int number, VariantProgress? progress)
    {
        var route = dungeon.Route(number);
        if (route == null)
            return;

        var done = progress?.Done.Contains(number) == true;
        Styling.Text($"Route {number} — {service.Title(dungeon, number)}", done ? CharonTheme.AccentMint : CharonTheme.TextStrong);
        if (done)
        {
            ImGui.SameLine();
            Styling.Text("(you have it)", CharonTheme.TextMuted);
        }

        Styling.Text($"{route.Path} path · ends at {route.Boss}{(route.Secret ? " · secret boss" : "")}", CharonTheme.TextDim);

        Styling.TextWrapped($"1. {dungeon.Forks[route.Path]}", CharonTheme.TextSecondary);
        for (var i = 0; i < route.Steps.Length; i++)
            Styling.TextWrapped($"{i + 2}. {route.Steps[i]}", CharonTheme.TextSecondary);

        Styling.TextWrapped("Pick the start the NPC suggests for an extra coffer. One record per run.", CharonTheme.TextMuted);
    }

    private static void DrawTable(VariantService service, VariantDungeon dungeon, VariantProgress? progress, int target,
        VariantService.GroupView group, Func<string, string> display)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable($"##variantRoutes{dungeon.Series}", 6, flags))
            return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 24f);
        ImGui.TableSetupColumn("Record", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Path", ImGuiTableColumnFlags.WidthFixed, 52f);
        ImGui.TableSetupColumn("Ends at", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Needed by", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableHeadersRow();

        foreach (var route in dungeon.Routes)
        {
            var done = progress?.Done.Contains(route.Number) == true;
            var color = done ? CharonTheme.AccentMint : CharonTheme.TextSecondary;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Styling.Text(route.Number.ToString(), color);
            ImGui.TableNextColumn();
            Styling.Text(service.Title(dungeon, route.Number) + (route.Secret ? "  (secret)" : ""), color);
            ImGui.TableNextColumn();
            Styling.Text(route.Path.ToString(), CharonTheme.TextDim);
            ImGui.TableNextColumn();
            Styling.Text(route.Boss, CharonTheme.TextDim);
            ImGui.TableNextColumn();
            var needed = VariantNeeds.NeededBy(dungeon, route.Number, group.Known);
            if (needed.Count == 0)
                Styling.Text(group.Known.Count == 0 ? "?" : "nobody", CharonTheme.TextMuted);
            else if (needed.Count == group.Known.Count && needed.Count > 1)
                Styling.Text($"everyone ({needed.Count})", CharonTheme.AccentAmber);
            else
                Styling.TextWrapped(Names(needed, display), CharonTheme.AccentAmber);
            ImGui.TableNextColumn();
            if (route.Number == target)
                Styling.Text("guiding", CharonTheme.Accent);
            else if (ImGui.SmallButton($"Guide##variant{dungeon.Series}r{route.Number}"))
                service.Choose(dungeon, route.Number);
        }

        ImGui.EndTable();
    }

    /// <summary>Who needs what, by the fork they start from: one row per toon, the route numbers still to do.</summary>
    private static void DrawByPath(VariantDungeon dungeon, VariantService.GroupView group, Func<string, string> display)
    {
        if (group.Known.Count == 0)
            return;

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable($"##variantPaths{dungeon.Series}", 5, flags))
            return;

        ImGui.TableSetupColumn("Toon", ImGuiTableColumnFlags.WidthStretch, 2f);
        foreach (var path in Enum.GetValues<VariantPath>())
            ImGui.TableSetupColumn($"{path} path", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Left to do", ImGuiTableColumnFlags.WidthFixed, 72f);
        ImGui.TableHeadersRow();

        var now = DateTime.UtcNow;
        foreach (var needs in VariantNeeds.ByToon(dungeon, group.Known))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Styling.Text(display(needs.Toon.Name), needs.Toon.Local ? CharonTheme.TextStrong : CharonTheme.TextSecondary);
            if (!needs.Toon.Local && ImGui.IsItemHovered())
                Styling.Tooltip($"Record reported {Age(now - needs.Toon.SeenUtc)} ago.");

            foreach (var path in Enum.GetValues<VariantPath>())
            {
                ImGui.TableNextColumn();
                var left = needs.ByPath[path];
                if (left.Count == 0)
                    Styling.Text("done", CharonTheme.AccentMint);
                else
                    Styling.Text(string.Join(", ", left), CharonTheme.AccentAmber);
            }

            ImGui.TableNextColumn();
            Styling.Text(needs.Total == 0 ? "complete" : needs.Total.ToString(),
                needs.Total == 0 ? CharonTheme.AccentMint : CharonTheme.TextSecondary);
        }

        ImGui.EndTable();
    }

    private static string Names(IEnumerable<string> names, Func<string, string> display) =>
        string.Join(", ", names.Select(display));

    private static string Age(TimeSpan age) =>
        age.TotalMinutes < 1 ? $"{age.TotalSeconds:0}s" : age.TotalHours < 1 ? $"{age.TotalMinutes:0}m" : $"{age.TotalHours:0.#}h";

    private static string Label(VariantService service, VariantDungeon dungeon, int number, VariantProgress? progress)
    {
        var mark = progress == null ? "" : progress.Done.Contains(number) ? "  [done]" : "";
        return $"{number}. {service.Title(dungeon, number)}{mark}";
    }

    /// <summary>Shown under the dungeons: what is drawn in the world, and what can't be.</summary>
    public static string MarkersNote =>
        "Inside the dungeon, the guided route's objects are labelled in the world: levers, statues, "
        + "the NPCs you must save or leave, and where to stand. Aloalo Island and the Merchant's Tale also get "
        + "fixed spots (from the Splatoon route layouts); Sil'dihn and Rokkon are labelled on their objects only.";

    /// <summary>The count used in the section heading.</summary>
    public static string Summary(VariantService service)
    {
        var bits = service.ReadRecord();
        if (bits == null)
            return "record not loaded yet";
        var done = VariantRoutes.All.Sum(d => VariantRecord.Progress(d, bits).Done.Count);
        return $"{done} of {VariantRoutes.All.Sum(d => d.Routes.Count)} records";
    }
}
