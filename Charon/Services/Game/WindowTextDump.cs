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
        report.Append("[TextDump] '").Append(addonName).Append("' has ").Append(ordered.Count)
            .Append(" text node(s):");

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
