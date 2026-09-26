using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Dumps an open window's text, with positions, and lists which windows are loaded at all.
///
/// This exists because of a rule this repo already learned the hard way: a window's layout is RECORDED from a
/// real look, never guessed. Reading a window's rows means knowing which node holds what, and guessing that
/// produces exactly the class of bug that is invisible in source and obvious in game — a garbled name, a flag
/// whose meaning was assumed. Two of those shipped in one day; this is the alternative.
///
/// The addon NAME is the other unknown, so it is answered the same way: <see cref="LoadedAddons"/> asks the
/// client which windows exist right now, so nobody has to guess that the game's Timers window is called
/// "Timers". Read-only throughout.
/// </summary>
public sealed unsafe class WindowTextDump
{
    private readonly IGameGui _gameGui;
    private readonly IPluginLog _log;

    public WindowTextDump(IGameGui gameGui, IPluginLog log)
    {
        _gameGui = gameGui;
        _log = log;
    }

    /// <summary>The last dump, so a caller can echo a preview into chat instead of only into the log.</summary>
    public IReadOnlyList<(int Index, float X, float Y, string Text)> LastDump { get; private set; } = [];

    public string LastDumpAddon { get; private set; } = string.Empty;

    /// <summary>What the last read actually saw — counts, and whether the root node was there at all.</summary>
    public string LastDiagnostics { get; private set; } = string.Empty;

    /// <summary>Every window the client currently has loaded, optionally only the visible ones.</summary>
    public IReadOnlyList<string> LoadedAddons(bool visibleOnly)
    {
        var names = new List<string>();

        try
        {
            var stage = AtkStage.Instance();
            if (stage == null)
                return names;

            var list = stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
            for (var i = 0; i < list.Count; i++)
            {
                var unit = list.Entries[i].Value;
                if (unit == null)
                    continue;

                if (visibleOnly && !unit->IsVisible)
                    continue;

                var name = unit->NameString.ToString();
                if (name.Length > 0)
                    names.Add(name);
            }
        }
        catch (Exception ex)
        {
            _log.Debug("[TextDump] the addon list could not be read: {0}", ex.Message);
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Turn what a person would type into the client's own addon name: exact match first, then the first
    /// loaded window containing the text. Typing "timer" should find "Timers" without anyone having to know.
    /// </summary>
    public string? Resolve(string nameOrFragment)
    {
        var loaded = LoadedAddons(visibleOnly: false);
        if (loaded.Count == 0)
            return null;

        var exact = loaded.FirstOrDefault(n => n.Equals(nameOrFragment, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        return loaded.FirstOrDefault(n => n.Contains(nameOrFragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every text node in the addon, in tree order, with its screen position. <paramref name="requireVisible"/>
    /// is the default because most reads are about what the player can see — but a search for a phrase wants
    /// every loaded window, including one mid-open whose visibility flag has not caught up.
    /// </summary>
    public IReadOnlyList<(int Index, float X, float Y, string Text)> Read(string addonName, bool requireVisible = true)
    {
        var lines = new List<(int, float, float, string)>();

        try
        {
            var unit = (AtkUnitBase*)_gameGui.GetAddonByName(addonName).Address;
            if (unit == null || (requireVisible && !unit->IsVisible))
                return lines;

            // Walk the node TREE rather than UldManager.NodeList: an addon whose rows live in a list component
            // keeps them as children of that component, and a flat NodeList can come back empty for exactly
            // those windows (which is what "_ToDoList has 0 text node(s)" was).
            var seen = new HashSet<nint>();
            Walk(unit->RootNode, lines, seen);

            if (lines.Count == 0)
            {
                LastDiagnostics = $"{addonName}: root={(unit->RootNode == null ? "null" : "ok")}, "
                                   + $"nodeList={unit->UldManager.NodeListCount} entries, 0 text nodes in the tree";
            }
            else
            {
                LastDiagnostics = $"{addonName}: {lines.Count} text node(s)";
            }
        }
        catch (Exception ex)
        {
            LastDiagnostics = $"{addonName}: {ex.Message}";
            _log.Debug("[TextDump] {0} could not be read: {1}", addonName, ex.Message);
        }

        return lines;
    }

    /// <summary>Depth-first over ChildNode / NextSiblingNode — the same shape the client itself walks.</summary>
    private static void Walk(AtkResNode* node, List<(int, float, float, string)> lines, HashSet<nint> seen)
    {
        while (node != null)
        {
            if (!seen.Add((nint)node))
                return; // a cycle here would spin forever, and this must never do that

            if (node->Type == NodeType.Text)
            {
                var text = ReadText((AtkTextNode*)node);
                if (text.Length > 0)
                    lines.Add((lines.Count, node->ScreenX, node->ScreenY, text));
            }

            if (node->ChildNode != null)
                Walk(node->ChildNode, lines, seen);

            node = node->NextSiblingNode;
        }
    }

    /// <summary>
    /// Find a phrase in ANY loaded window and say which one holds it.
    ///
    /// This is what to reach for when the window's name is unknown: instead of guessing a name and hoping, ask
    /// who is showing the text. Visible or not is deliberately ignored — a window mid-open is still the window.
    /// </summary>
    public IReadOnlyList<(string Addon, int Index, float X, float Y, string Text)> Find(string term)
    {
        var hits = new List<(string, int, float, float, string)>();

        foreach (var addon in LoadedAddons(visibleOnly: false))
        {
            foreach (var line in Read(addon, requireVisible: false))
            {
                if (line.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
                    hits.Add((addon, line.Index, line.X, line.Y, line.Text));
            }
        }

        return hits;
    }

    /// <summary>Per-window text counts for everything loaded — the shape of the evidence when a search misses.</summary>
    public IReadOnlyList<(string Addon, int TextNodes)> TextCounts()
    {
        var counts = new List<(string, int)>();

        foreach (var addon in LoadedAddons(visibleOnly: false))
        {
            counts.Add((addon, Read(addon, requireVisible: false).Count));
        }

        return counts;
    }

    /// <summary>Record a window's text: full detail to the log, and the lines kept for a chat preview.</summary>
    public bool Dump(string addonName)
    {
        var unit = (AtkUnitBase*)_gameGui.GetAddonByName(addonName).Address;
        if (unit == null || !unit->IsVisible)
        {
            LastDump = [];
            LastDumpAddon = addonName;
            _log.Information("[TextDump] '{0}' is not open — open it and run the command again.", addonName);
            return false;
        }

        // Ordered by row then column, so the table structure is visible in the log as rows rather than as
        // whatever order the node tree happens to be in.
        var ordered = Read(addonName)
            .OrderBy(l => Math.Round(l.Y / 4f))
            .ThenBy(l => l.X)
            .ToList();

        LastDump = ordered;
        LastDumpAddon = addonName;

        var report = new StringBuilder();
        report.Append("[TextDump] ").Append(LastDiagnostics).Append(':');
        if (ordered.Count == 0)
        {
            report.Append("\n  (nothing to list — a window whose rows are drawn by a component may keep "
                          + "them off the node tree entirely; that is a finding, not a failure)");
        }

        foreach (var line in ordered)
        {
            report.Append('\n').Append("  #").Append(line.Index)
                .Append("  x=").Append((int)line.X)
                .Append(" y=").Append((int)line.Y)
                .Append("  \"").Append(line.Text.Replace("\n", "\\n")).Append('"');
        }

        _log.Information("{0}", report.ToString());
        return true;
    }

    private static string ReadText(AtkTextNode* node)
    {
        try
        {
            // ExtractText is the supported accessor (Dalamud.Utility.Utf8StringExtensions). Calling ToString
            // on a Utf8String gives boxes — the same mistake that made the GC board's item names unreadable.
            return node->NodeText.ExtractText().Trim();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
