using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Charon.Services.Game;

/// <summary>
/// Reads the game's own weekly/daily allowance state for the Weeklies board. Sources verified in
/// Odysseus's production paths, not guessed:
///
/// - Custom Deliveries: <c>SatisfactionSupplyManager</c> — used-this-week is the sum of
///   <c>UsedAllowances</c>; "loaded" is any <c>SatisfactionRanks</c> entry being non-zero (a
///   character with a client unlocked always has rank ≥ 1, so all-zero means the arrays have not
///   been fetched yet, never "everyone is rank 0").
/// - Allied Society dailies: <c>QuestManager.GetBeastTribeAllowance()</c> — allowances LEFT today.
///
/// Fail-open: any read failure reports "not loaded" and the board says so rather than showing a
/// confident zero. 2s cache — the window and the sidebar dot both poll this every frame.
/// </summary>
public sealed unsafe class WeekliesReader
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(2);

    private readonly IPluginLog _log;

    private Snapshot _cached = new(false, 0, false, 0);
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public WeekliesReader(IPluginLog log)
    {
        _log = log;
    }

    public sealed record Snapshot(
        bool DeliveriesLoaded, int DeliveriesUsed,
        bool TribesLoaded, int TribeAllowanceLeft);

    public string Status { get; private set; } = "not read yet";

    public Snapshot Read(DateTime utcNow)
    {
        if (utcNow - _cachedAtUtc < CacheFor)
            return _cached;
        _cachedAtUtc = utcNow;

        var deliveriesLoaded = false;
        var deliveriesUsed = 0;
        var tribesLoaded = false;
        var tribeLeft = 0;

        try
        {
            var supply = SatisfactionSupplyManager.Instance();
            if (supply != null)
            {
                foreach (var rank in supply->SatisfactionRanks)
                {
                    if (rank > 0)
                    {
                        deliveriesLoaded = true;
                        break;
                    }
                }

                foreach (var used in supply->UsedAllowances)
                    deliveriesUsed += used;
            }

            var quests = QuestManager.Instance();
            if (quests != null)
            {
                tribesLoaded = true;
                tribeLeft = (int)quests->GetBeastTribeAllowance();
            }

            Status = $"deliveries {(deliveriesLoaded ? $"{deliveriesUsed} used" : "not loaded")} · "
                     + $"tribes {(tribesLoaded ? $"{tribeLeft} left" : "not loaded")}";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Weeklies read threw");
            Status = "read threw (see log)";
        }

        _cached = new Snapshot(deliveriesLoaded, deliveriesUsed, tribesLoaded, tribeLeft);
        return _cached;
    }
}
