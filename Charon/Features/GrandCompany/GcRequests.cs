using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Charon.Features.GrandCompany;

/// <summary>One item the Grand Company is asking for, as the delivery board last showed it.</summary>
/// <param name="Name">From the item sheet by id — the board's own name string carries icon payload glyphs.</param>
/// <param name="Job">The company's job for a supply row (CRP, GSM…), the gathering class for provisioning.</param>
public sealed record GcRequestEntry(uint ItemId, string Name, int Requested, GcMissionKind Kind, string Job);

/// <summary>
/// The day's Grand Company request list as a SNAPSHOT, with the time it was taken and when it rolls over.
///
/// The list belongs to the game, not to this plugin: a row disappears the moment that item is handed in, and the
/// whole list is replaced when the mission allowance renews. So this is only ever what the board said at
/// <see cref="CapturedUtc"/>, and no snapshot at all means UNKNOWN rather than "nothing is wanted today" — the
/// two answers that matter most to anything downstream are "here is the list" and "nobody has looked".
/// </summary>
public sealed record GcRequestSnapshot(
    string Character,
    DateTime CapturedUtc,
    DateTime? RolloverUtc,
    List<GcRequestEntry> Entries)
{
    /// <summary>Total units the company asked for across every row.</summary>
    public int Total => Entries.Sum(e => e.Requested);

    /// <summary>Crafts to make (Supply) and things to gather (Provisioning) — the split a crafter cares about.</summary>
    [JsonIgnore]
    public IReadOnlyList<GcRequestEntry> Supply => Entries.Where(e => e.Kind == GcMissionKind.Supply).ToList();

    [JsonIgnore]
    public IReadOnlyList<GcRequestEntry> Provisioning =>
        Entries.Where(e => e.Kind == GcMissionKind.Provisioning).ToList();
}

/// <summary>
/// Building, ageing and describing a request-list snapshot. Pure — a caller hands in what the board said, so
/// every rule here is tested without a client.
/// </summary>
public static class GcRequests
{
    /// <summary>With no rollover time to go on, a snapshot older than this is treated as from a previous day.</summary>
    public static readonly TimeSpan AssumeStaleAfter = TimeSpan.FromHours(22);

    /// <summary>
    /// A snapshot from the rows the board was showing. Rows with no item or nothing requested are dropped, and a
    /// duplicate item keeps the LARGEST request — the same item can be asked for under both supply and
    /// provisioning, and the demand is what has to be met, not the smaller of two numbers.
    /// </summary>
    /// <summary>
    /// A snapshot survives a config round-trip. It is persisted in the plugin config, so a shape the serializer
    /// writes but cannot read back would lose the day's list on the next reload — silently, since a missing
    /// snapshot looks exactly like one that was never taken.
    /// </summary>
    public static GcRequestSnapshot? FromJson(string json) =>
        JsonSerializer.Deserialize<GcRequestSnapshot>(json);

    public static GcRequestSnapshot Build(
        string character,
        DateTime capturedUtc,
        DateTime? rolloverUtc,
        IEnumerable<GcRequestEntry> entries) =>
        new(character,
            capturedUtc,
            rolloverUtc,
            entries
                .Where(e => e.ItemId != 0 && e.Requested > 0)
                .GroupBy(e => e.ItemId)
                .Select(g => g.OrderByDescending(e => e.Requested).First())
                .OrderBy(e => e.Kind)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());

    /// <summary>
    /// What still has to be MADE or GATHERED: what the company asked for minus what is already held. This is the
    /// list a crafter would work from, and it is the point of keeping the snapshot at all.
    /// </summary>
    public static IReadOnlyList<(GcRequestEntry Entry, int Shortfall)> Demand(
        GcRequestSnapshot? snapshot,
        Func<uint, int> held)
    {
        if (snapshot == null)
            return [];

        return snapshot.Entries
            .Select(e => (e, Math.Max(0, e.Requested - held(e.ItemId))))
            .ToList();
    }

    /// <summary>
    /// Has the day moved on since this was taken? Either the rollover time has passed, or the snapshot is old
    /// enough that it is from a previous day — the second case exists because a character who never opens the
    /// board still has an old snapshot sitting there.
    /// </summary>
    public static bool IsStale(GcRequestSnapshot? snapshot, DateTime nowUtc) =>
        snapshot != null
        && (snapshot.RolloverUtc is { } rollover && nowUtc >= rollover
            || nowUtc - snapshot.CapturedUtc > AssumeStaleAfter);

    /// <summary>One line for the UI or the log: what the snapshot is and how old it is.</summary>
    public static string Describe(GcRequestSnapshot? snapshot, DateTime nowUtc)
    {
        if (snapshot == null)
            return "no request list has been captured yet — open the delivery board and take one";

        var age = nowUtc - snapshot.CapturedUtc;
        var when = age.TotalMinutes switch
        {
            < 1 => "just now",
            < 90 => $"{age.TotalMinutes:0} min ago",
            _ => $"{age.TotalHours:0} h ago",
        };

        var state = IsStale(snapshot, nowUtc)
            ? " — from a previous day, the list has rolled over since"
            : snapshot.RolloverUtc is { } rollover
                ? $" — rolls over {rollover:ddd HH:mm}"
                : string.Empty;

        return $"{snapshot.Entries.Count} item(s), {snapshot.Total} unit(s), captured {when}{state}";
    }

    /// <summary>
    /// The IPC payload. EXTEND-ONLY: fields are added, never renamed or removed, because a caller that cannot
    /// read this simply has no Grand Company materials and must not break when the shape grows.
    /// </summary>
    public static string ToJson(GcRequestSnapshot? snapshot, Func<uint, int> held, DateTime nowUtc)
    {
        if (snapshot == null)
        {
            // Explicitly unknown, not empty: a caller must be able to tell "nobody has looked at the board" from
            // "the company wants nothing today", because the second is a real answer and the first is not.
            return JsonSerializer.Serialize(new
            {
                known = false,
                note = "no Grand Company request list has been captured on this machine yet",
            });
        }

        return JsonSerializer.Serialize(new
        {
            known = true,
            character = snapshot.Character,
            capturedUtc = snapshot.CapturedUtc.ToString("O"),
            rolloverUtc = snapshot.RolloverUtc?.ToString("O"),
            stale = IsStale(snapshot, nowUtc),
            total = snapshot.Total,
            items = Demand(snapshot, held).Select(d => new
            {
                itemId = d.Entry.ItemId,
                name = d.Entry.Name,
                requested = d.Entry.Requested,
                shortfall = d.Shortfall,
                kind = d.Entry.Kind.ToString(),
                job = d.Entry.Job,
            }).ToList(),
        });
    }
}
