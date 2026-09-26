using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Charon.Features.Containers;
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
    private readonly Func<IReadOnlyList<RetainerBag>> _retainers;
    private readonly IPluginLog _log;

    public ChestContentsIpc(
        IDalamudPluginInterface pluginInterface,
        Func<ChestSnapshot?> chest,
        Func<IReadOnlyList<RetainerBag>> retainers,
        IPluginLog log)
    {
        _chest = chest;
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

    public void Dispose()
    {
        _getContents.UnregisterFunc();
        _status.UnregisterFunc();
        _getItem.UnregisterFunc();
    }
}
