using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Dumps an open window's text, with positions, to the log.
///
/// This exists because of a rule this repo already learned the hard way: a window's layout is RECORDED from a
/// real look, never guessed. Reading a window's rows means knowing which node holds what, and guessing that
/// produces exactly the class of bug that is invisible in source and obvious in game — a garbled name, a
/// column read from the wrong field, a flag whose meaning was assumed. Two of those were shipped in one day;
/// this is the alternative.
///
/// Read-only, and useful for any future window read: open the window, run <c>/charon text &lt;addon&gt;</c>, and
/// the log has every text node with its coordinates, so the row structure can be seen rather than assumed.
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

    /// <summary>Every text node in the addon, in node order, with its screen position.</summary>
    public IReadOnlyList<(int Index, float X, float Y, string Text)> Read(string addonName)
    {
        var lines = new List<(int, float, float, string)>();

        try
        {
            var unit = (AtkUnitBase*)_gameGui.GetAddonByName(addonName).Address;
            if (unit == null || !unit->IsVisible)
                return lines;

            var list = unit->UldManager.NodeList;
            var count = unit->UldManager.NodeListCount;

            for (var i = 0; i < count; i++)
            {
                var node = list[i];
                if (node == null || node->Type != NodeType.Text)
                    continue;

                var text = ReadText((AtkTextNode*)node);
                if (text.Length == 0)
                    continue;

                lines.Add((i, node->ScreenX, node->ScreenY, text));
            }
        }
        catch (Exception ex)
        {
            _log.Debug("[TextDump] {0} could not be read: {1}", addonName, ex.Message);
        }

        return lines;
    }

    /// <summary>Log a window's text so it can be read back from /xllog — the recording half of the rule.</summary>
    public bool Dump(string addonName)
    {
        var unit = (AtkUnitBase*)_gameGui.GetAddonByName(addonName).Address;
        if (unit == null || !unit->IsVisible)
        {
            _log.Information("[TextDump] '{0}' is not open — open it and run the command again.", addonName);
            return false;
        }

        var lines = Read(addonName);
        var report = new StringBuilder();
        report.Append("[TextDump] '").Append(addonName).Append("' has ").Append(lines.Count).Append(" text node(s):");

        // One line per node, ordered by row then column so the table structure is visible in the log.
        foreach (var line in lines.OrderBy(l => Math.Round(l.Y / 4f)).ThenBy(l => l.X))
        {
            report.Append('\n').Append("  #").Append(line.Index)
                .Append("  x=").Append((int)line.X)
                .Append(" y=").Append((int)line.Y)
                .Append("  \"").Append(line.Text.Replace("\n", "\\n")).Append('"');
        }

        _log.Information("{0}", report.ToString());
        return true;
    }

    /// <summary>The names of every visible addon that currently has text — for when the addon name is the unknown.</summary>
    public IReadOnlyList<string> VisibleAddonNames(IEnumerable<string> candidates) =>
        candidates
            .Where(name =>
            {
                var unit = (AtkUnitBase*)_gameGui.GetAddonByName(name).Address;
                return unit != null && unit->IsVisible;
            })
            .ToList();

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
