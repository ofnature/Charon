using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Charon.Features.Containers;

/// <summary>Where one box is holding a stack: bags, saddlebag, the FC chest, or a named retainer.</summary>
public sealed record FleetHolding(string Where, int Nq, int Hq);

/// <summary>One box's reply about one item.</summary>
public sealed record FleetAnswer(
    string Character,
    bool CanTrade,
    DateTime SeenUtc,
    IReadOnlyList<FleetHolding> Holdings)
{
    public int Nq => Holdings.Sum(h => h.Nq);
    public int Hq => Holdings.Sum(h => h.Hq);
    public int Total => Nq + Hq;
}

/// <summary>
/// Collects the fleet's answers to "who has this item?". Pure — no Dalamud types.
///
/// The rule that shapes everything here: **a box that has not answered is UNKNOWN, never zero.**
/// The relay is a LAN broadcast with no delivery guarantee, a box may be zoning, loading or simply
/// off, and a planner that reads silence as "they have none" will craft something the fleet already
/// owns — or worse, conclude an item cannot be sourced and stop. So the report always says how many
/// boxes replied, and is only <see cref="FleetSearchReport.Complete"/> once the window has closed.
///
/// <see cref="FleetAnswer.CanTrade"/> travels with each answer because a free trial account cannot
/// trade, use the market board or join a free company. Its items exist but cannot be moved to
/// anyone, so totals that ignored that would promise materials no plan can ever collect.
/// </summary>
public sealed class FleetItemSearch
{
    private readonly Dictionary<string, FleetAnswer> _answers = new(StringComparer.OrdinalIgnoreCase);

    private string _id = string.Empty;
    private uint _itemId;
    private DateTime _startedUtc = DateTime.MinValue;
    private TimeSpan _window = TimeSpan.Zero;

    /// <summary>Whether a search is currently open (asked, window not yet elapsed).</summary>
    public bool Running(DateTime nowUtc) => _id.Length > 0 && nowUtc - _startedUtc < _window;

    public string Id => _id;

    public uint ItemId => _itemId;

    /// <summary>Begins a search. Any answers still arriving for a previous one are discarded by id.</summary>
    public void Start(string id, uint itemId, DateTime nowUtc, TimeSpan window)
    {
        _id = id;
        _itemId = itemId;
        _startedUtc = nowUtc;
        _window = window;
        _answers.Clear();
    }

    /// <summary>
    /// Records a reply. Answers carrying a different id are DROPPED: a late answer to a previous
    /// question would otherwise be counted against the current one, which is how a search reports
    /// holdings of an item nobody asked about.
    /// </summary>
    public bool Record(string id, string character, bool canTrade, DateTime seenUtc, IReadOnlyList<FleetHolding> holdings)
    {
        if (_id.Length == 0 || !string.Equals(id, _id, StringComparison.Ordinal) || character.Length == 0)
            return false;

        // Last answer from a box wins, so a re-send corrects rather than doubling the count.
        _answers[character] = new FleetAnswer(character, canTrade, seenUtc, holdings ?? []);
        return true;
    }

    /// <summary>The report as it stands. Safe to call at any time; says whether it is final.</summary>
    public FleetSearchReport Report(DateTime nowUtc)
    {
        var answers = _answers.Values
            .OrderByDescending(a => a.Total)
            .ThenBy(a => a.Character, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var complete = _id.Length > 0 && !Running(nowUtc);
        return new FleetSearchReport(_itemId, complete, answers);
    }
}

/// <summary>What the fleet said, and how much of the fleet actually said it.</summary>
public sealed record FleetSearchReport(uint ItemId, bool Complete, IReadOnlyList<FleetAnswer> Answers)
{
    public int Answered => Answers.Count;

    public int TotalNq => Answers.Sum(a => a.Nq);

    public int TotalHq => Answers.Sum(a => a.Hq);

    /// <summary>
    /// What a plan may actually COUNT ON: free trial toons hold items nobody can be given, so their
    /// stacks are reported but never included here.
    /// </summary>
    public int ReachableNq => Answers.Where(a => a.CanTrade).Sum(a => a.Nq);

    public int ReachableHq => Answers.Where(a => a.CanTrade).Sum(a => a.Hq);

    /// <summary>The oldest capture behind any answer — how stale the worst of this picture is.</summary>
    public DateTime? OldestSeenUtc =>
        Answers.Count == 0 ? null : Answers.Min(a => a.SeenUtc);

    public string Summarize()
    {
        if (Answers.Count == 0)
            return Complete ? "no box answered" : "asking the fleet...";

        var total = TotalNq + TotalHq;
        var reachable = ReachableNq + ReachableHq;
        var locked = total - reachable;
        var lockedText = locked > 0 ? $" ({locked} on a free trial toon, unreachable)" : string.Empty;
        var state = Complete ? string.Empty : " so far";

        return $"{total} across {Answers.Count} box{(Answers.Count == 1 ? "" : "es")}{state}{lockedText}";
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        itemId = ItemId,
        complete = Complete,
        answered = Answered,
        totalNq = TotalNq,
        totalHq = TotalHq,
        reachableNq = ReachableNq,
        reachableHq = ReachableHq,
        oldestSeenUtc = OldestSeenUtc?.ToString("O"),
        summary = Summarize(),
        boxes = Answers.Select(a => new
        {
            character = a.Character,
            canTrade = a.CanTrade,
            seenUtc = a.SeenUtc.ToString("O"),
            nq = a.Nq,
            hq = a.Hq,
            holdings = a.Holdings.Select(h => new { where = h.Where, nq = h.Nq, hq = h.Hq }),
        }),
    });
}
