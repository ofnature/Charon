using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.GrandCompany;
using Charon.Features.Dailies;
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
    string Status,
    string AllowanceText = "",
    DateTime? AllowanceSeenUtc = null,
    bool? DailiesOpen = null,
    string AllowanceStatus = "",
    bool SelectedTabEmpty = false,
    string Notice = "");

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

    /// <summary>The addon that states the day's hand-in state in words, not just in rows.</summary>
    private const string BoardAddon = "GrandCompanySupplyList";

    private readonly RetainerContentsReader _contents;
    private readonly VentureSheetReader _sheet;
    private readonly AllowanceReader _allowances;
    private readonly WindowTextDump _windows;
    private readonly Func<ulong> _contentId;
    private readonly IPluginLog _log;

    private GcBoard? _cached;
    private DateTime _cachedAtUtc = DateTime.MinValue;
    private bool _loggedBoard;

    public GcDailiesReader(
        RetainerContentsReader contents,
        VentureSheetReader sheet,
        AllowanceReader allowances,
        WindowTextDump windows,
        Func<ulong> contentId,
        IPluginLog log)
    {
        _contents = contents;
        _sheet = sheet;
        _allowances = allowances;
        _windows = windows;
        _contentId = contentId;
        _log = log;
    }

    /// <summary>
    /// Take a snapshot of the day's request list — what the company is asking for, which is what a crafter needs.
    ///
    /// Null when the board is not open: a snapshot of a board nobody is looking at would be a list of zero items,
    /// and "the company wants nothing" is not the same answer as "nobody has looked".
    /// </summary>
    public GcRequestSnapshot? CaptureRequests(DateTime nowLocal)
    {
        var board = Read();
        if (!board.Open || board.Missions.Count == 0)
            return null;

        var entries = board.Missions
            .Where(m => m.ItemId != 0)
            .Select(m => new GcRequestEntry(m.ItemId, m.ItemName, m.Requested, m.Kind, m.Job));

        return GcRequests.Build(
            _contentId().ToString(),
            DateTime.UtcNow,
            Allowances.Rollover(_allowances.MissionAllowance?.Value, nowLocal),
            entries);
    }

    /// <summary>
    /// Everything the delivery window is saying: its text nodes AND its value strings, because the day's state
    /// arrives as a value pair ("You possess no applicable items." / "No more deliveries are being accepted
    /// today.") rather than as a text node.
    /// </summary>
    private IReadOnlyList<string> BoardLines()
    {
        var lines = _windows.Read(BoardAddon, requireVisible: false).Select(t => t.Text).ToList();
        lines.AddRange(_windows.ValueTexts.Where(v => v.IsText).Select(v => v.Text));
        return lines;
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
            var reported = agent->NumItems;
            var count = Math.Clamp(reported, 0, 256);

            for (var i = 0; i < count; i++)
            {
                var row = agent->ItemArray[i];
                if (row.ItemId == 0)
                    continue;

                // The name comes from the item sheet, not from the row's own string: this struct holds its
                // name as UI text (payload-coded), which renders as boxes, and the sheet is also what the
                // rest of Charon shows for an item id.
                var name = _sheet.ItemName(row.ItemId);

                missions.Add(new GcDailyMission(
                    row.Position,
                    GcDailies.KindFor(row.Position),
                    GcDailies.JobFor(row.Position),
                    row.ItemId,
                    name,
                    row.NumRequested,
                    row.ExpReward,
                    row.SealReward,
                    row.NumPossessed,
                    row.IsBonusReward,
                    row.IsTurnInAvailable,
                    row.TurnInAvailable));
            }

            // Once per session, the raw row state goes to the log. The availability byte's meaning for
            // supply and provisioning rows is not documented anywhere, and the honest way to learn it is to
            // look at what the game says beside what the board shows — not to guess and then word around it.
            if (!_loggedBoard && missions.Count > 0)
            {
                _loggedBoard = true;
                _log.Debug("[GC] board: {0}", string.Join(" | ", missions.Select(m =>
                    $"{m.Job}#{m.Position} id={m.ItemId} req={m.Requested} own={m.Possessed} "
                    + $"flag={m.AvailabilityRaw} bonus={(m.BonusReward ? 1 : 0)} avail={m.TurnInAvailable}")));
            }

            var counts = GcDailies.Counts(missions);
            var allowance = _allowances.MissionAllowance;

            // The window's own words, when it is open: the game stating the day's state beats this reader
            // inferring it from a countdown, which is the mistake the verdict line made once already.
            var windowLines = open ? BoardLines() : [];
            var notice = open ? GcDailies.Notice(windowLines) ?? string.Empty : string.Empty;

            // Empty is a property of the tab on screen, never of the value array: Supply can hold eight rows
            // while the Expert tab — the one showing "You possess no applicable items." — is the visible one.
            var tabRows = missions.Count(m => GcDailies.TabOfPosition(m.Position) == agent->SelectedTab);
            var tabEmpty = open && missions.Count > 0 && tabRows == 0;

            return new GcBoard(missions, seals, maxSeals, company, rank, agent->SelectedTab, open,
                $"{counts.Supply} supply · {counts.Provisioning} provisioning · {counts.Expert} expert"
                + $" (agent reports {reported})"
                + (open ? string.Empty : " — open the board at your GC officer to refresh"),
                allowance?.Value ?? string.Empty,
                _allowances.SeenUtc == DateTime.MinValue ? null : _allowances.SeenUtc,
                _allowances.DailiesOpen,
                _allowances.Status,
                tabEmpty,
                notice);
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
