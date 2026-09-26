using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.GrandCompany;
using Charon.Features.Retainers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace Charon.Services.Game;

/// <summary>The board as read: the missions plus the rank/seal context they sit in.</summary>
public sealed record GcBoard(
    IReadOnlyList<GcDailyMission> Missions,
    int Seals,
    int MaxSeals,
    byte Company,
    int Rank,
    int SelectedTab,
    bool Open,
    string Status);

/// <summary>
/// Reads the Grand Company delivery board — Supply, Provisioning and Expert Delivery — from the game's own
/// data rather than from the window's pixels.
///
/// The values live in <c>AgentGrandCompanySupply</c>: <c>ItemArray</c> holds every row (positions 0-7 supply,
/// 8-10 provisioning, 11+ expert delivery, which is the client's own numbering) with the item, how many are
/// requested, what the game counts as possessed, the exp and seal rewards, the bonus flag and the
/// turn-in-available flag. That is the documented struct — AutoRetainer reads the same rows through a raw
/// pointer at <c>addon + 648</c>, which this repo does not do: a window pointer plus an index is exactly the
/// shape that breaks on a patch, and the SDK already carries the layout.
///
/// The held counts are the interesting part: the game's own column counts your INVENTORY only, so a row reads
/// 0/0 while the item sits in a retainer's bags. Both counts are answered here — bags from the inventory
/// manager, retainers from the contents store.
/// </summary>
public sealed unsafe class GcDailiesReader
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(2);

    private readonly RetainerContentsReader _contents;
    private readonly IPluginLog _log;

    private GcBoard? _cached;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public GcDailiesReader(RetainerContentsReader contents, IPluginLog log)
    {
        _contents = contents;
        _log = log;
    }

    /// <summary>The last read's status line, for the Debug page.</summary>
    public string Status { get; private set; } = "not read yet";

    public GcBoard Read()
    {
        if (_cached != null && DateTime.UtcNow - _cachedAtUtc < CacheFor)
            return _cached;

        var board = ReadBoard();
        _cached = board;
        _cachedAtUtc = DateTime.UtcNow;
        Status = board.Status;
        return board;
    }

    /// <summary>What is in the player's own bags, as the game counts it (HQ included).</summary>
    public int InBags(uint itemId) =>
        itemId == 0 ? 0 : InventoryManager.Instance()->GetInventoryItemCount(itemId);

    /// <summary>What the retainers hold, HQ included — the number the board's own column cannot see.</summary>
    public int InRetainers(uint itemId)
    {
        if (itemId == 0)
            return 0;

        var (nq, hq) = RetainerContents.Total(_contents.Bags(), itemId);
        return nq + hq;
    }

    private GcBoard ReadBoard()
    {
        try
        {
            var player = PlayerState.Instance();
            var company = player == null ? (byte)0 : player->GrandCompany;
            var rank = player == null ? 0 : player->GetGrandCompanyRank();
            var seals = company == 0 ? 0 : (int)InventoryManager.Instance()->GetCompanySeals(company);
            var maxSeals = company == 0 ? 0 : (int)InventoryManager.Instance()->GetMaxCompanySeals(company);

            var agent = AgentGrandCompanySupply.Instance();
            if (agent == null)
            {
                return Empty(company, rank, seals, maxSeals, "the delivery board agent is not available yet");
            }

            var open = agent->IsAddonShown();
            var missions = new List<GcDailyMission>();
            var count = Math.Clamp(agent->NumItems, 0, 128);

            for (var i = 0; i < count; i++)
            {
                var row = agent->ItemArray[i];
                if (row.ItemId == 0)
                    continue;

                missions.Add(new GcDailyMission(
                    row.Position,
                    GcDailies.KindFor(row.Position),
                    GcDailies.JobFor(row.Position),
                    row.ItemId,
                    row.ItemName.ToString(),
                    row.NumRequested,
                    row.ExpReward,
                    row.SealReward,
                    row.NumPossessed,
                    row.IsBonusReward,
                    row.IsTurnInAvailable));
            }

            return new GcBoard(missions, seals, maxSeals, company, rank, agent->SelectedTab, open,
                open
                    ? $"{missions.Count} row(s) on the board"
                    : $"{missions.Count} row(s) known — open the board at your GC officer to refresh them");
        }
        catch (Exception ex)
        {
            _log.Debug("[GC] board read failed: {0}", ex.Message);
            return Empty(0, 0, 0, 0, $"the board could not be read: {ex.Message}");
        }
    }

    private static GcBoard Empty(byte company, int rank, int seals, int maxSeals, string status) =>
        new([], seals, maxSeals, company, rank, -1, false, status);

    /// <summary>Per-row plans with both held counts filled in — what the section draws.</summary>
    public IReadOnlyList<GcMissionPlan> Plans(GcBoard board) =>
        GcDailies.PlanAll(board.Missions, InBags, InRetainers);
}
