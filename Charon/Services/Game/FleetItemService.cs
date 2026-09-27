using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Charon.Features.Containers;
using Charon.Features.Retainers;
using Charon.Ipc;
using Charon.Services;

namespace Charon.Services.Game;

/// <summary>
/// "Who in the fleet has this item?", answered across MACHINES.
///
/// Nothing else can answer it. Dalamud IPC reaches only plugins inside the same client, and XA
/// Database — which already stores inventory, saddlebag, armoury and retainer contents per
/// character — keeps one SQLite file per MACHINE, so its cross-character search stops at the edge
/// of the PC it runs on. The fleet spans two. The LAN relay is the only channel that crosses them,
/// and Charon is the only plugin here that has one.
///
/// So this is a BRIDGE, not a second store: every box answers from what it can see locally, and the
/// asking box merges the replies. A box that stays silent is reported as silent, never as empty.
/// </summary>
public sealed unsafe class FleetItemService
{
    /// <summary>How long a search stays open for replies. The relay is LAN — this is generous.</summary>
    private static readonly TimeSpan AnswerWindow = TimeSpan.FromSeconds(3);

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    private readonly RelayClient _relay;
    private readonly ICondition _condition;
    private readonly Func<string> _localName;
    private readonly Func<ChestSnapshot?> _chest;
    private readonly Func<IReadOnlyList<RetainerBag>> _retainers;
    private readonly IPluginLog _log;

    private readonly FleetItemSearch _search = new();

    public FleetItemService(
        RelayClient relay,
        ICondition condition,
        Func<string> localName,
        Func<ChestSnapshot?> chest,
        Func<IReadOnlyList<RetainerBag>> retainers,
        IPluginLog log)
    {
        _relay = relay;
        _condition = condition;
        _localName = localName;
        _chest = chest;
        _retainers = retainers;
        _log = log;
    }

    public string Status { get; private set; } = "no search yet";

    /// <summary>The fleet's answer as it stands, final or not.</summary>
    public FleetSearchReport Report(DateTime nowUtc) => _search.Report(nowUtc);

    /// <summary>
    /// Asks the fleet. Returns the search id; answers arrive over the next few seconds, so a caller
    /// polls <see cref="Report"/> rather than expecting a value here.
    /// </summary>
    public string Ask(uint itemId, DateTime nowUtc)
    {
        if (itemId == 0)
        {
            Status = "nothing asked for";
            return string.Empty;
        }

        var id = Guid.NewGuid().ToString("N")[..8];
        _search.Start(id, itemId, nowUtc, AnswerWindow);

        var me = _localName();
        _relay.Publish(RelayClient.ItemsChannel, ItemRelay.Ask(me, id, itemId));

        // Answer our own question directly: the relay never delivers a frame back to its sender.
        RecordLocal(id, itemId, nowUtc);

        Status = $"asked the fleet about {itemId}";
        _log.Debug("[Items] ask {0} for {1}", id, itemId);
        return id;
    }

    /// <summary>Relay frames for the items channel. Ignores anything malformed.</summary>
    public void OnRelayMessage(string channel, string json)
    {
        if (!string.Equals(channel, RelayClient.ItemsChannel, StringComparison.Ordinal))
            return;

        var message = ItemRelay.Parse(json);
        if (message == null)
            return;

        try
        {
            if (message.Act == ItemRelay.ActAsk)
                AnswerAsk(message);
            else if (message.Act == ItemRelay.ActAnswer)
                RecordAnswer(message);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Items] relay frame threw");
        }
    }

    /// <summary>Someone asked; reply with what this box can see.</summary>
    private void AnswerAsk(ItemMessage ask)
    {
        var (holdings, seenUtc) = LocalHoldings(ask.ItemId, DateTime.UtcNow);
        if (holdings.Count == 0)
            return; // nothing to contribute — a box with none says nothing rather than spamming zeroes

        _relay.Publish(RelayClient.ItemsChannel, ItemRelay.Answer(
            _localName(), ask.Id, ask.ItemId, CanTrade, seenUtc.ToString("O", CultureInfo.InvariantCulture), holdings));

        Status = $"answered {ask.From} about {ask.ItemId}";
    }

    private void RecordAnswer(ItemMessage answer)
    {
        var seen = DateTime.TryParse(
            answer.SeenUtc, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTime.UtcNow;

        var holdings = answer.Holdings
            .Select(h => new FleetHolding(h.Where, h.Nq, h.Hq))
            .ToList();

        if (_search.Record(answer.Id, answer.From, answer.CanTrade, seen, holdings))
            Status = _search.Report(DateTime.UtcNow).Summarize();
    }

    private void RecordLocal(string id, uint itemId, DateTime nowUtc)
    {
        var (holdings, seenUtc) = LocalHoldings(itemId, nowUtc);
        if (holdings.Count == 0)
            return;

        _search.Record(
            id, _localName(), CanTrade, seenUtc,
            holdings.Select(h => new FleetHolding(h.Where, h.Nq, h.Hq)).ToList());
    }

    /// <summary>
    /// What this box can see: live bags, plus the FC chest and retainer SNAPSHOTS. The returned
    /// timestamp is the OLDEST source used — bags are live, but a retainer nobody has opened this
    /// month is not, and the honest answer is the worst of them.
    /// </summary>
    private (List<RelayHolding> Holdings, DateTime SeenUtc) LocalHoldings(uint itemId, DateTime nowUtc)
    {
        var holdings = new List<RelayHolding>();
        var oldest = nowUtc;

        var (bagNq, bagHq) = BagCounts(itemId);
        if (bagNq + bagHq > 0)
            holdings.Add(new RelayHolding { Where = ItemRelay.WhereBags, Nq = bagNq, Hq = bagHq });

        var chest = _chest();
        if (chest != null)
        {
            var pages = ChestContents.Find(chest, itemId);
            var nq = pages.Sum(p => p.Nq);
            var hq = pages.Sum(p => p.Hq);
            if (nq + hq > 0)
            {
                holdings.Add(new RelayHolding { Where = ItemRelay.WhereChest, Nq = nq, Hq = hq });
                if (chest.CapturedUtc < oldest)
                    oldest = chest.CapturedUtc;
            }
        }

        foreach (var bag in _retainers())
        {
            var nq = bag.Stacks.Where(s => s.ItemId == itemId && !s.Hq).Sum(s => s.Qty);
            var hq = bag.Stacks.Where(s => s.ItemId == itemId && s.Hq).Sum(s => s.Qty);
            if (nq + hq == 0)
                continue;

            holdings.Add(new RelayHolding
            {
                Where = ItemRelay.WhereRetainerPrefix + bag.Name,
                Nq = nq,
                Hq = hq,
            });

            if (bag.CapturedUtc < oldest)
                oldest = bag.CapturedUtc;
        }

        return (holdings, oldest);
    }

    /// <summary>
    /// Whether this character can be part of a transfer at all. A free trial account cannot trade,
    /// use the market board or join a free company, so its items can neither be sent out nor
    /// received — live game state, so there is nothing to configure and nothing to drift.
    /// </summary>
    private bool CanTrade => !_condition[ConditionFlag.OnFreeTrial];

    private (int Nq, int Hq) BagCounts(uint itemId)
    {
        var nq = 0;
        var hq = 0;

        foreach (var bag in PlayerBags)
        {
            try
            {
                var container = InventoryManager.Instance()->GetInventoryContainer(bag);
                if (container == null || !container->IsLoaded)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId != itemId)
                        continue;

                    if (slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality))
                        hq += slot->Quantity;
                    else
                        nq += slot->Quantity;
                }
            }
            catch
            {
                // container unreadable mid-transition — skip it
            }
        }

        return (nq, hq);
    }
}
