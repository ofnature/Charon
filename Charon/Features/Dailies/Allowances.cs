using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Dailies;

/// <summary>What a Timers-window allowance line says, in the game's own words.</summary>
public enum AllowanceState
{
    /// <summary>Nothing there yet, or the window is not open.</summary>
    Unknown,

    /// <summary>"Available Now" — nothing is pending.</summary>
    Available,

    /// <summary>A countdown to when the next allowance arrives.</summary>
    Countdown,

    /// <summary>"None" — the game has nothing to offer, e.g. no squadron.</summary>
    None,

    /// <summary>"… Incomplete", e.g. the Masked Carnivale.</summary>
    Incomplete,
}

/// <param name="Hours">Hours until the next allowance; 0 when it is available or unknown.</param>
/// <param name="Minutes">Minutes past those hours, as the window shows them (75:15 is 75 h 15 m, not clamped).</param>
public sealed record AllowanceLine(string Label, string Value, AllowanceState State, int Hours, int Minutes)
{
    public bool Available => State == AllowanceState.Available;

    /// <summary>One line for the UI, saying what the game says and no more.</summary>
    public string Describe() => State switch
    {
        AllowanceState.Available => "available now",
        AllowanceState.Countdown => $"{Hours}h {Minutes:00}m remaining",
        AllowanceState.None => "none",
        AllowanceState.Incomplete => "incomplete",
        _ => "not read",
    };
}

/// <summary>
/// Reading a value out of the game's own Timers window.
///
/// By LABEL, not by node index: the window's rows are found by the text the game prints ("Next Mission
/// Allowance"), and the value is whatever else sits on that row. That is deliberately layout-tolerant — the
/// one thing this repo has learned not to do is assume which node holds what — and it means the same reader
/// works for every line in that window (ventures, Doman Enclave, Custom Deliveries, the map allowance).
///
/// Pure: the caller supplies the window's text nodes with their positions, so the association and the parsing
/// are tested without a client.
/// </summary>
public static class Allowances
{
    /// <summary>Two nodes are on the same row when their tops are within this many pixels.</summary>
    public const float RowTolerance = 4f;

    /// <summary>The lines the game prints in its Timers window, in the order they appear.</summary>
    public static readonly string[] KnownLabels =
    [
        "Next Mission Allowance",
        "Custom Deliveries",
        "Fashion Report",
        "Adventurer Squadron",
        "Doman Enclave",
        "The Masked Carnivale",
        "Ventures",
        "Next Map Allowance",
        "Next Allied Society",
        "Next Leve Allowance",
    ];

    /// <summary>
    /// Find a labelled line: the row carrying the label, and the value text on that row (or, when the window
    /// puts the value under the label, the next text node after it).
    /// </summary>
    public static AllowanceLine? Find(
        IReadOnlyList<(float X, float Y, string Text)> nodes,
        string label)
    {
        var rows = Group(nodes);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var labelNode = row.FirstOrDefault(t => t.Text.Contains(label, StringComparison.OrdinalIgnoreCase));
            if (labelNode.Text is null)
                continue;

            // The game sometimes prints label and value as one string ("Next Map Allowance: Available Now").
            var labelText = labelNode.Text;
            string? value = null;
            var colon = labelText.IndexOf(':');
            if (colon > 0 && colon < labelText.Length - 1)
            {
                value = labelText[(colon + 1)..].Trim();
                labelText = labelText[..colon].Trim();
            }

            // Otherwise the value is on the same row: prefer the text the game words as an allowance, because
            // a row can carry a second figure ("Custom Deliveries 75:15 Remaining … Allowances: 12") and the
            // rightmost one is not always the answer. Fall back to the rightmost when nothing parses.
            value ??= row.Where(t => t != labelNode && Parse(t.Text) != AllowanceState.Unknown)
                .OrderByDescending(t => t.X)
                .Select(t => t.Text)
                .FirstOrDefault(v => v.Length > 0)
                ?? row.Where(t => t != labelNode)
                    .OrderByDescending(t => t.X)
                    .Select(t => t.Text)
                    .FirstOrDefault(v => v.Length > 0);

            if (value is null && i + 1 < rows.Count)
            {
                var next = rows[i + 1];
                if (!next.Any(t => KnownLabels.Any(k => t.Text.Contains(k, StringComparison.OrdinalIgnoreCase))))
                    value = next.OrderBy(t => t.X).Select(t => t.Text).FirstOrDefault(v => v.Length > 0);
            }

            return new AllowanceLine(labelText, value ?? string.Empty, Parse(value), Hours(value), Minutes(value));
        }

        return null;
    }

    /// <summary>The game's wording, turned into a state. Anything unrecognised stays Unknown.</summary>
    public static AllowanceState Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return AllowanceState.Unknown;

        if (value.Contains("available now", StringComparison.OrdinalIgnoreCase))
            return AllowanceState.Available;

        if (value.Contains("remaining", StringComparison.OrdinalIgnoreCase))
            return AllowanceState.Countdown;

        if (value.Contains("incomplete", StringComparison.OrdinalIgnoreCase))
            return AllowanceState.Incomplete;

        if (value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            return AllowanceState.None;

        return AllowanceState.Unknown;
    }

    /// <summary>Hours out of "15:15 Remaining" (and 75:15 is 75 hours — the game does not clamp).</summary>
    public static int Hours(string? value)
    {
        var parts = Split(value);
        return parts?.Hours ?? 0;
    }

    public static int Minutes(string? value)
    {
        var parts = Split(value);
        return parts?.Minutes ?? 0;
    }

    private static (int Hours, int Minutes)? Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var colon = value.IndexOf(':');
        if (colon <= 0)
            return null;

        var left = value[..colon].Trim();
        if (!int.TryParse(left, out var hours))
            return null;

        var rest = value[(colon + 1)..].Trim();
        var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var minutes) ? (hours, minutes) : null;
    }

    private static List<List<(float X, float Y, string Text)>> Group(
        IReadOnlyList<(float X, float Y, string Text)> nodes)
    {
        var rows = new List<List<(float X, float Y, string Text)>>();

        foreach (var node in nodes.OrderBy(n => n.Y).ThenBy(n => n.X))
        {
            var row = rows.LastOrDefault();
            if (row != null && Math.Abs(row[0].Y - node.Y) <= RowTolerance)
                row.Add(node);
            else
                rows.Add([node]);
        }

        return rows;
    }
}
