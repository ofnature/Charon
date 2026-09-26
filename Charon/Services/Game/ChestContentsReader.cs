using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.Containers;
using Charon.Features.Retainers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Charon.Services.Game;

/// <summary>
/// Keeps the free company chest's contents as a SNAPSHOT in the config, so anything — this plugin later, or
/// another plugin over IPC — can ask what the chest holds without the chest being open.
///
/// It takes an open chest to read one: the client fills those pages only once the chest has been opened on this
/// machine, which is the same restriction the retainer store lives under. A page whose container is not loaded is
/// NOT recorded as empty, because "the chest holds none of these" and "nobody has looked" must not be the same
/// answer to a crafter deciding where to draw materials from.
///
/// Cheap: a visible chest addon and a 20-second floor between reads, so a chest left open costs a few container
/// walks a minute and nothing else.
/// </summary>
public sealed unsafe class ChestContentsReader
{
    private static readonly TimeSpan CaptureEvery = TimeSpan.FromSeconds(20);

    /// <summary>Both addon names: the chest window has a large variant when the player expands it.</summary>
    private static readonly string[] ChestAddons = ["FreeCompanyChest", "FreeCompanyChestLarge"];

    private static readonly InventoryType[] Pages =
    [
        InventoryType.FreeCompanyPage1, InventoryType.FreeCompanyPage2, InventoryType.FreeCompanyPage3,
        InventoryType.FreeCompanyPage4, InventoryType.FreeCompanyPage5,
    ];

    private readonly IGameGui _gameGui;
    private readonly CharonConfig _config;
    private readonly Func<ulong> _contentId;
    private readonly Action _save;
    private readonly IPluginLog _log;

    private DateTime _lastCaptureUtc = DateTime.MinValue;

    public ChestContentsReader(
        IGameGui gameGui,
        CharonConfig config,
        Func<ulong> contentId,
        Action save,
        IPluginLog log)
    {
        _gameGui = gameGui;
        _config = config;
        _contentId = contentId;
        _save = save;
        _log = log;
    }

    public string Status { get; private set; } = "no chest contents captured yet";

    /// <summary>The local character's chest as last seen, or null when nobody has opened it here.</summary>
    public ChestSnapshot? Local
    {
        get
        {
            if (!_config.ChestContents.TryGetValue(_contentId().ToString(), out var stored))
                return null;

            return new ChestSnapshot(
                stored.CapturedUtc,
                stored.Pages
                    .Select(p => new ChestPage(
                        p.Page,
                        p.Stacks.Select(s => new RetainerStackCount(s.ItemId, s.Qty, s.Hq)).ToList()))
                    .ToList());
        }
    }

    public bool ChestOpen
    {
        get
        {
            foreach (var name in ChestAddons)
            {
                var addon = _gameGui.GetAddonByName(name);
                if (addon.IsNull)
                    continue;

                var unit = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon.Address;
                if (unit != null && unit->IsVisible)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Called every tick. Only reads when a chest is actually open, and then at most every 20 seconds — the
    /// contents only change while it is open, so anything faster is pure cost.
    /// </summary>
    public void Update(DateTime nowUtc)
    {
        if (!ChestOpen)
            return;

        if (nowUtc - _lastCaptureUtc < CaptureEvery)
            return;

        _lastCaptureUtc = nowUtc;
        Capture(nowUtc);
    }

    /// <summary>Read the pages now, whatever the throttle says. Returns false when nothing could be recorded.</summary>
    public bool Capture(DateTime nowUtc)
    {
        try
        {
            var pages = new List<CharonConfig.ChestPageState>();

            for (var i = 0; i < Pages.Length; i++)
            {
                var container = InventoryManager.Instance()->GetInventoryContainer(Pages[i]);
                if (container == null || !container->IsLoaded)
                    continue;

                var stacks = new List<CharonConfig.RetainerStack>();
                for (var slot = 0; slot < container->Size; slot++)
                {
                    var item = container->GetInventorySlot(slot);
                    if (item == null || item->ItemId == 0 || item->Quantity <= 0)
                        continue;

                    stacks.Add(new CharonConfig.RetainerStack
                    {
                        ItemId = item->ItemId,
                        Qty = item->Quantity,
                        Hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0,
                    });
                }

                // An empty page that loaded is recorded as empty — that is a real answer. A page that did not
                // load is simply absent, and a caller reading the payload can see which pages it got.
                pages.Add(new CharonConfig.ChestPageState { Page = i + 1, Stacks = stacks });
            }

            // Nothing loaded: the window is up but the client has not filled the pages yet. Recording that as an
            // empty chest would be a lie no caller could detect.
            if (pages.Count == 0)
            {
                Status = "the chest is open but its pages are not loaded yet — nothing recorded";
                return false;
            }

            _config.ChestContents[_contentId().ToString()] = new CharonConfig.ChestState
            {
                CapturedUtc = nowUtc,
                Pages = pages,
            };

            _save();

            Status = $"captured {pages.Count} page(s), {pages.Sum(p => p.Stacks.Count)} stack(s)";
            _log.Debug("[Chest] {0}", Status);
            return true;
        }
        catch (Exception ex)
        {
            Status = $"the chest could not be read: {ex.Message}";
            _log.Debug(ex, "[Chest] read failed");
            return false;
        }
    }
}
