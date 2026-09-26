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
    /// The labels that identify the TIMERS WINDOW itself, as opposed to any window that happens to word-match a
    /// single row. A window is only accepted while it shows at least one of these AND several labels at once.
    /// </summary>
    public static readonly string[] Signatures =
    [
        "Next Mission Allowance",
        "Next Leve Allowance",
        "Next Map Allowance",
        "Fashion Report",
        "The Masked Carnivale",
        "Adventurer Squadron",
        "Doman Enclave",
        "Custom Deliveries",
    ];

    /// <summary>
    /// Label/value pairs out of the window's value array.
    ///
    /// The Timers window's rows arrive here as a text value, then a small int (the row's kind), then the value
    /// text — verified against a dump of the live window, where those values are literally
    /// "Next Mission Allowance" / int 2 / " 14:02 Remaining  (9/26 15:00)". So a pair is a text value followed by
    /// another text value within the next two slots. Extra pairs come out of this (a value followed by the next
    /// row's label), which costs nothing: a caller only accepts pairs whose label IT recognises.
    ///
    /// Pure, so the shape is tested without a client.
    /// </summary>
    public static IReadOnlyList<(string Label, string Value)> Pairs(IReadOnlyList<(bool IsText, string Text)> values)
    {
        var pairs = new List<(string Label, string Value)>();

        for (var i = 0; i < values.Count; i++)
        {
            if (!values[i].IsText || values[i].Text.Length == 0)
                continue;

            for (var gap = 1; gap <= 2 && i + gap < values.Count; gap++)
            {
                var next = values[i + gap];
                if (!next.IsText)
                    continue;

                if (next.Text.Length > 0)
                    pairs.Add((values[i].Text.Trim(), next.Text.Trim()));

                break;
            }
        }

        return pairs;
    }

    /// <summary>
    /// Does a node's text CARRY this label? It has to START with it.
    ///
    /// "Contains" was loose enough to accept the wrong window: "No ventures in progress." contains the label
    /// "Ventures", so a window that merely mentioned ventures counted as one of the Timers rows — which is how a
    /// scan latched onto a different addon entirely and reported three unrelated lines as allowances.
    /// </summary>
    public static bool IsLabel(string text, string label) =>
        text.TrimStart().StartsWith(label, StringComparison.OrdinalIgnoreCase);

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
            var labelNode = row.FirstOrDefault(t => t.Text is not null && IsLabel(t.Text, label));
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

            if (value is null)
            {
                // Nothing on the row parsed, which usually means the label and its value did not land on the
                // same row: nodes reached through a list component carry component-relative positions, so the
                // same visual line can differ by a few pixels. Widen once, keeping only text to the RIGHT of the
                // label (the value's side) and stopping short of the next row's height, so a wider net cannot
                // borrow the neighbouring line's figure.
                var nearby = nodes
                    .Where(t => t.Text.Length > 0
                                && !(Math.Abs(t.X - labelNode.X) < 0.5f && Math.Abs(t.Y - labelNode.Y) < 0.5f)
                                && Math.Abs(t.Y - labelNode.Y) <= RowTolerance * 3
                                && t.X > labelNode.X)
                    .OrderBy(t => t.X)
                    .ToList();

                value = nearby.Where(t => Parse(t.Text) != AllowanceState.Unknown).Select(t => t.Text).FirstOrDefault()
                        ?? nearby.Select(t => t.Text).FirstOrDefault();
            }

            if (value is null && i + 1 < rows.Count)
            {
                var next = rows[i + 1];
                if (!next.Any(t => t.Text is not null && KnownLabels.Any(k => IsLabel(t.Text, k))))
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

    /// <summary>
    /// When a line's countdown ends, from the stamp the window prints in parentheses — "(9/26 15:00)" in
    /// " 14:02 Remaining  (9/26 15:00)". The year is not printed, so the current one is assumed and a date more
    /// than a day in the past is taken to be next year's: a countdown never ends in the past.
    ///
    /// This is what makes a request-list snapshot know when it stops being today's list, instead of inferring it
    /// from a fixed number of hours.
    /// </summary>
    public static DateTime? Rollover(string? value, DateTime nowLocal)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var open = value.IndexOf('(');
        var close = value.IndexOf(')', open + 1);
        if (open < 0 || close < open)
            return null;

        var inside = value[(open + 1)..close].Trim();
        var parts = inside.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return null;

        var date = parts[0].Split('/');
        var time = parts[1].Split(':');
        if (date.Length != 2 || time.Length != 2
            || !int.TryParse(date[0], out var month) || !int.TryParse(date[1], out var day)
            || !int.TryParse(time[0], out var hour) || !int.TryParse(time[1], out var minute))
            return null;

        if (month is < 1 or > 12 || day is < 1 or > 31 || hour is < 0 or > 23 || minute is < 0 or > 59)
            return null;

        try
        {
            var stamp = new DateTime(nowLocal.Year, month, day, hour, minute, 0, DateTimeKind.Local);
            return stamp < nowLocal.AddDays(-1) ? stamp.AddYears(1) : stamp;
        }
        catch (ArgumentOutOfRangeException)
        {
            // e.g. 2/30 — the window said something this reader does not understand, so it says so by answering
            // nothing rather than by guessing a date.
            return null;
        }
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
