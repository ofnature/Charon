using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Dims FC chest slots that don't match a search query by writing the game's own node alpha —
/// the technique FCCH ships (AGPL — mechanism reimplemented here from the addon facts, no code
/// taken): the 50 grid slots of the FreeCompanyChest addon are nodes id 23..72, mapping 1:1 to
/// the displayed page container's slots, and the five tab radio buttons sit at NodeList indices
/// 101 (tab 1) down to 97 (tab 5). A non-matching filled slot gets alpha 80, a match stays 255.
///
/// Applied every frame while a query is live (self-healing if the game redraws); one restore
/// pass when the query clears. Tab buttons only dim when their page is LOADED and holds no
/// match — an unviewed tab is unknown, and unknown must never look like "nothing here".
/// </summary>
public sealed unsafe class ChestSearchFilter : IDisposable
{
    private const string ChestAddonName = "FreeCompanyChest";
    private const uint FirstSlotNodeId = 23;
    private const int SlotsPerPage = 50;
    private const byte DimAlpha = 80;
    private const byte FullAlpha = 255;


    private readonly IGameGui _gameGui;
    private readonly IDataManager _dataManager;
    private readonly IPluginLog _log;
    private readonly Dictionary<uint, string> _nameCache = new();

    private bool _dimmed;

    public ChestSearchFilter(IGameGui gameGui, IDataManager dataManager, IPluginLog log)
    {
        _gameGui = gameGui;
        _dataManager = dataManager;
        _log = log;
    }

    public string Status { get; private set; } = "idle";

    public void Update(string query)
    {
        try
        {
            var addon = _gameGui.GetAddonByName(ChestAddonName);
            if (addon.IsNull || !addon.IsVisible)
            {
                // Addon gone — its nodes died with it, nothing to restore.
                _dimmed = false;
                Status = "chest closed";
                return;
            }

            var unit = (AtkUnitBase*)addon.Address;
            if (string.IsNullOrWhiteSpace(query))
            {
                if (_dimmed)
                {
                    RestoreAll(unit);
                    _dimmed = false;
                }

                Status = "idle";
                return;
            }

            Apply(unit, query.Trim());
            _dimmed = true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Chest search filter threw");
            Status = "threw (see log)";
        }
    }

    public void Dispose()
    {
        // Plugin unloading with the chest open: leave the window as we found it.
        try
        {
            var addon = _gameGui.GetAddonByName(ChestAddonName);
            if (!addon.IsNull && _dimmed)
                RestoreAll((AtkUnitBase*)addon.Address);
        }
        catch
        {
            // best-effort
        }
    }

    private void Apply(AtkUnitBase* unit, string query)
    {
        var page = FcChestUi.CurrentPage(unit);
        var container = page >= 1
            ? InventoryManager.Instance()->GetInventoryContainer((InventoryType)((int)InventoryType.FreeCompanyPage1 + page - 1))
            : null;
        var matches = 0;

        for (var i = 0; i < SlotsPerPage; i++)
        {
            var node = unit->GetNodeById(FirstSlotNodeId + (uint)i);
            if (node == null)
                continue;

            var alpha = FullAlpha;
            if (container != null && container->IsLoaded && i < container->Size)
            {
                var item = container->GetInventorySlot(i);
                if (item != null && item->ItemId != 0)
                {
                    if (Matches(item->ItemId, query))
                        matches++;
                    else
                        alpha = DimAlpha;
                }
            }

            node->Color.A = alpha;
        }

        DimTabs(unit, query);
        Status = $"filtering '{query}' — {matches} on this page";
    }

    /// <summary>Dim tab buttons whose LOADED page holds no match; unviewed pages stay bright.</summary>
    private void DimTabs(AtkUnitBase* unit, string query)
    {
        for (var tab = 0; tab < 5; tab++)
        {
            var index = FcChestUi.Tab1NodeIndex - tab;
            if (index >= unit->UldManager.NodeListCount)
                continue;

            var node = unit->UldManager.NodeList[index];
            if (node == null)
                continue;

            var container = InventoryManager.Instance()->GetInventoryContainer((InventoryType)((int)InventoryType.FreeCompanyPage1 + tab));
            var known = container != null && container->IsLoaded;
            node->Color.A = known && !PageHasMatch(container, query) ? DimAlpha : FullAlpha;
        }
    }

    private bool PageHasMatch(InventoryContainer* container, string query)
    {
        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item != null && item->ItemId != 0 && Matches(item->ItemId, query))
                return true;
        }

        return false;
    }

    private void RestoreAll(AtkUnitBase* unit)
    {
        for (var i = 0; i < SlotsPerPage; i++)
        {
            var node = unit->GetNodeById(FirstSlotNodeId + (uint)i);
            if (node != null)
                node->Color.A = FullAlpha;
        }

        for (var tab = 0; tab < 5; tab++)
        {
            var index = FcChestUi.Tab1NodeIndex - tab;
            if (index >= unit->UldManager.NodeListCount)
                continue;

            var node = unit->UldManager.NodeList[index];
            if (node != null)
                node->Color.A = FullAlpha;
        }
    }


    private bool Matches(uint itemId, string query)
    {
        if (!_nameCache.TryGetValue(itemId, out var name))
        {
            var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            name = sheet != null && sheet.TryGetRow(itemId, out var row) ? row.Name.ExtractText() : string.Empty;
            _nameCache[itemId] = name;
        }

        return name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
