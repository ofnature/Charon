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
/// machine, which is the same restriction the retainer store lives under. A page that has not been read WITH
/// stacks is NOT recorded, because "the chest holds none of these" and "nobody has looked" must not be the same
/// answer to a crafter deciding where to draw materials from.
///
/// THE TRAP (live, 2026-11-03): a chest page reports its container as loaded but reads EMPTY until its data
/// arrives, a few seconds after the window opens, and a tab nobody has clicked stays empty for as long as the chest
/// is open. One read at the instant the window opened, then a 20-second floor, saved five empty pages over a chest
/// holding ~47k items. So the pages are re-read every 2 seconds while the chest is open, folded in per page by
/// <see cref="ChestContents.Merge"/> (an empty read never replaces a page that has stacks), and saved only when the
/// result changes.
/// </summary>
public sealed unsafe class ChestContentsReader
{
    private static readonly TimeSpan CaptureEvery = TimeSpan.FromSeconds(2);

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

    /// <summary>
    /// A read confirmed the stored contents without changing them: the capture time moved in memory and is saved
    /// once the chest closes, rather than writing the whole config every 2 seconds just to move a timestamp.
    /// </summary>
    private bool _stampUnsaved;

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

    /// <summary>
    /// The local character's chest as last seen, or null when nothing is known. Pages without stacks are left out:
    /// older builds saved the empty reads this class now refuses, and those are unknown, not empty.
    /// </summary>
    public ChestSnapshot? Local
    {
        get
        {
            var stored = StoredPages(out var capturedUtc);
            if (stored == null)
                return null;

            var known = stored.Where(p => p.Stacks.Count > 0).ToList();
            return known.Count == 0 ? null : new ChestSnapshot(ChestContents.OldestSeen(known, capturedUtc), known);
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
    /// Called every tick. Only reads while a chest is actually open, every 2 seconds — often enough to catch the
    /// pages filling in (and each tab as it is clicked) before the window closes; a read is five container walks.
    /// </summary>
    public void Update(DateTime nowUtc)
    {
        if (!ChestOpen)
        {
            if (_stampUnsaved)
            {
                _stampUnsaved = false;
                _save();
            }

            return;
        }

        if (nowUtc - _lastCaptureUtc < CaptureEvery)
            return;

        _lastCaptureUtc = nowUtc;
        Capture(nowUtc);
    }

    /// <summary>Read the pages now, whatever the throttle says. Returns false when the read found nothing to record.</summary>
    public bool Capture(DateTime nowUtc)
    {
        try
        {
            var read = new List<ChestPage>();

            for (var i = 0; i < Pages.Length; i++)
            {
                var container = InventoryManager.Instance()->GetInventoryContainer(Pages[i]);
                if (container == null || !container->IsLoaded)
                    continue;

                var stacks = new List<RetainerStackCount>();
                for (var slot = 0; slot < container->Size; slot++)
                {
                    var item = container->GetInventorySlot(slot);
                    if (item == null || item->ItemId == 0 || item->Quantity <= 0)
                        continue;

                    stacks.Add(new RetainerStackCount(
                        item->ItemId,
                        item->Quantity,
                        (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0));
                }

                read.Add(new ChestPage(i + 1, stacks, nowUtc));
            }

            var filled = read.Where(p => p.Stacks.Count > 0).Select(p => p.Page).ToList();
            var (merged, changed) = ChestContents.Merge(StoredPages(out _), read);
            var key = _contentId().ToString();

            if (changed)
            {
                if (merged.Count == 0)
                {
                    // Only empty pages were stored (an older build's empty read): that is nothing known.
                    _config.ChestContents.Remove(key);
                }
                else
                {
                    _config.ChestContents[key] = new CharonConfig.ChestState
                    {
                        // The oldest page's time, not now: pages kept from an earlier visit are that old.
                        CapturedUtc = ChestContents.OldestSeen(merged, nowUtc),
                        Pages = merged
                            .Select(p => new CharonConfig.ChestPageState
                            {
                                Page = p.Page,
                                SeenUtc = p.SeenUtc,
                                Stacks = p.Stacks
                                    .Select(s => new CharonConfig.RetainerStack { ItemId = s.ItemId, Qty = s.Qty, Hq = s.Hq })
                                    .ToList(),
                            })
                            .ToList(),
                    };
                }

                _stampUnsaved = false;
                _save();
            }
            else if (filled.Count > 0 && _config.ChestContents.TryGetValue(key, out var same))
            {
                // Same contents: only the pages read just now are confirmed, so only their times move.
                foreach (var page in same.Pages)
                {
                    if (page.SeenUtc == default)
                        page.SeenUtc = same.CapturedUtc;
                    if (filled.Contains(page.Page))
                        page.SeenUtc = nowUtc;
                }

                same.CapturedUtc = same.Pages.Min(p => p.SeenUtc);
                _stampUnsaved = true;
            }

            Status = Describe(read, filled, merged, changed);
            if (changed)
                _log.Debug("[Chest] {0}", Status);

            return filled.Count > 0 || changed;
        }
        catch (Exception ex)
        {
            Status = $"the chest could not be read: {ex.Message}";
            _log.Debug(ex, "[Chest] read failed");
            return false;
        }
    }

    /// <summary>The stored pages as read from the config, empty ones included (Merge drops them), or null.</summary>
    private List<ChestPage>? StoredPages(out DateTime capturedUtc)
    {
        capturedUtc = default;
        if (!_config.ChestContents.TryGetValue(_contentId().ToString(), out var stored))
            return null;

        capturedUtc = stored.CapturedUtc;
        var whole = stored.CapturedUtc;
        return stored.Pages
            .Select(p => new ChestPage(
                p.Page,
                p.Stacks.Select(s => new RetainerStackCount(s.ItemId, s.Qty, s.Hq)).ToList(),
                p.SeenUtc == default ? whole : p.SeenUtc))
            .ToList();
    }

    private static string Describe(
        IReadOnlyList<ChestPage> read,
        IReadOnlyList<int> filled,
        IReadOnlyList<ChestPage> merged,
        bool changed)
    {
        if (read.Count == 0)
            return "the chest is open but its pages are not loaded yet — nothing recorded";

        var waiting = read.Select(p => p.Page).Except(filled).ToList();
        var known = merged.Count == 0
            ? "nothing known yet"
            : $"store holds page(s) {string.Join(", ", merged.Select(p => p.Page))}, {merged.Sum(p => p.Slots)} stack(s)";

        var reading = filled.Count == 0
            ? "every page read empty so far (data not here yet, or tab not clicked) — kept as last seen"
            : $"read page(s) {string.Join(", ", filled)}"
                + (waiting.Count == 0 ? "" : $"; {string.Join(", ", waiting)} empty so far, kept as last seen");

        return $"{reading} · {known}{(changed ? " · saved" : "")}";
    }
}
