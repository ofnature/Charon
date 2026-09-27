using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Charon.Features.Containers;
using Charon.Services.Game;
using Charon.Features.Retainers;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Charon.Ipc;

/// <summary>
/// Where an item is, for a plugin that needs materials and cannot look itself — built for Hephaestus, whose
/// source chain is "inventory, FC chest, retainers, sub-crafts, then vendors".
///
/// | Charon.Chest.GetContentsJson     | Func&lt;string&gt;       | every captured chest page: stacks (HQ its own flag), units, capturedUtc, and whether anything is known at all |
/// | Charon.Chest.Status              | Func&lt;string&gt;       | one line: pages, stacks, units and how old the snapshot is                                                       |
/// | Charon.Containers.GetItemJson    | Func&lt;uint, string&gt; | ONE answer across both stores: per retainer NQ/HQ with its age, per chest page NQ/HQ, and the total             |
///
/// TWO THINGS CALLERS MUST DESIGN AROUND. First, every answer is a SNAPSHOT: the client fills retainer bags only
/// after that retainer's window has been opened at a bell, and the chest pages only after the chest has been
/// opened, so each payload carries its timestamp and an absent container is UNKNOWN, never empty. Second, the two
/// stores are keyed differently on purpose — retainers by name, the chest by page — because that is how the game
/// presents them, and a caller that wants a flat total reads <c>total</c>.
///
/// Read-only: there are no operations here, because both stores fill from windows the player opens. Nothing in
/// this class moves an item or spends anything.
///
/// Contract rule: EXTEND-ONLY, the same rule as the retainer gate and Hephaestus's own IPC. Callers fail open —
/// with these gates absent, a caller simply has no chest or retainer materials, exactly as before.
/// </summary>
public sealed class ChestContentsIpc : IDisposable
{
    private readonly ICallGateProvider<string> _getContents;
    private readonly ICallGateProvider<string> _status;
    private readonly ICallGateProvider<uint, string> _getItem;

    private readonly Func<ChestSnapshot?> _chest;
    private readonly ICallGateProvider<uint, int, bool, bool> _requestFetch;
    private readonly ICallGateProvider<bool> _fetchBusy;
    private readonly ICallGateProvider<string> _fetchStatus;
    private readonly FcChestManager _fcChest;
    private readonly Func<bool> _executeEnabled;
    private readonly ICallGateProvider<uint, bool> _askFleet;
    private readonly ICallGateProvider<string> _fleetResult;
    private readonly FleetItemService _fleetItems;
    private readonly Func<IReadOnlyList<RetainerBag>> _retainers;
    private readonly IPluginLog _log;

    public ChestContentsIpc(
        IDalamudPluginInterface pluginInterface,
        Func<ChestSnapshot?> chest,
        Func<IReadOnlyList<RetainerBag>> retainers,
        FcChestManager fcChest,
        Func<bool> executeEnabled,
        FleetItemService fleetItems,
        IPluginLog log)
    {
        _chest = chest;
        _fcChest = fcChest;
        _executeEnabled = executeEnabled;
        _fleetItems = fleetItems;

        _askFleet = pluginInterface.GetIpcProvider<uint, bool>("Charon.Containers.AskFleet");
        _fleetResult = pluginInterface.GetIpcProvider<string>("Charon.Containers.GetFleetJson");

        _askFleet.RegisterFunc(AskFleet);
        _fleetResult.RegisterFunc(FleetResult);

        _requestFetch = pluginInterface.GetIpcProvider<uint, int, bool, bool>("Charon.Chest.RequestFetch");
        _fetchBusy = pluginInterface.GetIpcProvider<bool>("Charon.Chest.FetchBusy");
        _fetchStatus = pluginInterface.GetIpcProvider<string>("Charon.Chest.FetchStatus");

        _requestFetch.RegisterFunc(RequestFetch);
        _fetchBusy.RegisterFunc(() => _executeEnabled() && _fcChest.Busy);
        _fetchStatus.RegisterFunc(() => _fcChest.LastOperation);
        _retainers = retainers;
        _log = log;

        _getContents = pluginInterface.GetIpcProvider<string>("Charon.Chest.GetContentsJson");
        _getContents.RegisterFunc(() => Safe(() => ChestContents.ToJson(_chest(), DateTime.UtcNow)));

        _status = pluginInterface.GetIpcProvider<string>("Charon.Chest.Status");
        _status.RegisterFunc(() => Safe(() => ChestContents.Describe(_chest(), DateTime.UtcNow)));

        _getItem = pluginInterface.GetIpcProvider<uint, string>("Charon.Containers.GetItemJson");
        _getItem.RegisterFunc(itemId => Safe(() => ItemJson(itemId)));
    }

    /// <summary>
    /// Every place Charon knows this item is, across both stores. The point of the gate: a crafter asking "do we
    /// already have any of this" should not have to know which plugin read which window.
    /// </summary>
    /// <summary>The last thing a caller asked of us, for the Debug line.</summary>
    public string Status { get; private set; } = "no calls yet";

    private string ItemJson(uint itemId)
    {
        var now = DateTime.UtcNow;
        var chest = _chest();

        var holdings = _retainers()
            .Select(bag =>
            {
                var nq = bag.Stacks.Where(s => s.ItemId == itemId && !s.Hq).Sum(s => s.Qty);
                var hq = bag.Stacks.Where(s => s.ItemId == itemId && s.Hq).Sum(s => s.Qty);
                return (bag, nq, hq);
            })
            .Where(x => x.nq > 0 || x.hq > 0)
            .Select(x => new
            {
                retainer = x.bag.Name,
                nq = x.nq,
                hq = x.hq,
                capturedUtc = x.bag.CapturedUtc.ToString("O"),
                ageMinutes = Math.Round((now - x.bag.CapturedUtc).TotalMinutes, 1),
            })
            .ToList();

        var pages = ChestContents.Find(chest, itemId)
            .Select(p => new { page = p.Page, nq = p.Nq, hq = p.Hq })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            itemId,
            retainers = holdings,
            retainerKnown = _retainers().Count,
            chest = new
            {
                known = chest != null,
                capturedUtc = chest?.CapturedUtc.ToString("O"),
                pages,
            },
            total = new
            {
                nq = holdings.Sum(h => h.nq) + pages.Sum(p => p.nq),
                hq = holdings.Sum(h => h.hq) + pages.Sum(h => h.hq),
            },
        });
    }

    /// <summary>No gate may throw into a caller's poll: a failure is an "unknown" payload with a reason.</summary>
    private string Safe(Func<string> build)
    {
        try
        {
            return build();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Containers IPC] failed to build an answer");
            return "{\"known\":false,\"note\":\"the container store could not be read this time\"}";
        }
    }

    /// <summary>
    /// Withdraw from the FC chest for another plugin. The chest is the source BEFORE retainers in
    /// a crafter's order, and unlike a retainer it needs no bell — only the chest window open.
    ///
    /// HQ-ONLY IS REFUSED, not silently substituted: the underlying withdraw drains NQ first and
    /// then HQ, so it cannot promise HQ units, and handing a crafter NQ when it asked for HQ would
    /// quietly cost it quality. <paramref name="highQuality"/> false means "any quality", the same
    /// meaning the retainer fetch gives it.
    /// </summary>
    private bool RequestFetch(uint itemId, int quantity, bool highQuality)
    {
        if (!_executeEnabled())
        {
            Status = "RequestFetch → refused (execution disabled)";
            return false;
        }

        if (highQuality)
        {
            Status = "RequestFetch → refused (HQ-only withdraw is not supported)";
            return false;
        }

        if (itemId == 0 || quantity <= 0)
        {
            Status = "RequestFetch → refused (nothing asked for)";
            return false;
        }

        if (_fcChest.Busy)
        {
            Status = "RequestFetch → refused (busy)";
            return false;
        }

        // The store says WHICH page holds it; the withdraw itself re-reads the live page, so a
        // stale snapshot costs a refusal rather than a wrong move.
        var pages = ChestContents.Find(_chest(), itemId);
        if (pages.Count == 0)
        {
            Status = $"RequestFetch → refused (no page is known to hold {itemId})";
            return false;
        }

        var page = pages.OrderByDescending(p => p.Nq + p.Hq).First().Page;
        var queued = _fcChest.StartWithdrawAmount(page, itemId, quantity);
        var started = queued > 0;

        Status = started
            ? $"RequestFetch → {itemId} x{quantity} from page {page}: {queued} move(s)"
            : $"RequestFetch → refused ({_fcChest.LastOperation})";
        _log.Debug("[Chest] IPC fetch {0} x{1} page {2} → {3} move(s)", itemId, quantity, page, queued);
        return started;
    }

    /// <summary>
    /// Asks every box on the LAN who holds an item. ASYNCHRONOUS on purpose: the answers come
    /// back over a few seconds, so this returns whether the question went out, and the caller
    /// polls GetFleetJson. Reading is free — no execute switch — because asking moves nothing.
    /// </summary>
    private bool AskFleet(uint itemId)
    {
        var id = _fleetItems.Ask(itemId, DateTime.UtcNow);
        Status = id.Length > 0 ? $"AskFleet {itemId}" : $"AskFleet {itemId} → refused";
        return id.Length > 0;
    }

    /// <summary>
    /// The fleet's answer so far. `complete` says whether the window has closed; until it has,
    /// a box that has not replied is simply MISSING, never counted as holding none.
    /// </summary>
    private string FleetResult()
    {
        Status = "GetFleetJson";
        return Safe(() => _fleetItems.Report(DateTime.UtcNow).ToJson());
    }

    public void Dispose()
    {
        _getContents.UnregisterFunc();
        _status.UnregisterFunc();
        _getItem.UnregisterFunc();
        _requestFetch.UnregisterFunc();
        _fetchBusy.UnregisterFunc();
        _fetchStatus.UnregisterFunc();
        _askFleet.UnregisterFunc();
        _fleetResult.UnregisterFunc();
    }
}
