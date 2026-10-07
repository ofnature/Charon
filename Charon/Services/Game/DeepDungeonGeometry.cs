using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.DeepDungeon;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;

namespace Charon.Services.Game;

/// <summary>
/// Loads a deep-dungeon floor set's room boxes from its <c>planmap.lgb</c> (see <see cref="FloorRooms"/>
/// for the verified layout and the BossMod attribution). Cached per territory; a zone whose file is
/// missing or unreadable caches an EMPTY result, which the filter treats as "show everything".
/// </summary>
public sealed class DeepDungeonGeometry
{
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly Dictionary<uint, IReadOnlyList<IReadOnlyList<RoomBox>>> _cache = new();

    public DeepDungeonGeometry(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log = log;
    }

    public IReadOnlyList<IReadOnlyList<RoomBox>> Layouts(uint territory)
    {
        if (_cache.TryGetValue(territory, out var cached))
            return cached;

        var layouts = Load(territory);
        _cache[territory] = layouts;
        _log.Debug("DD geometry: territory {0} → {1} layout(s), {2} box(es)",
            territory, layouts.Count, layouts.Sum(l => l.Count));
        return layouts;
    }

    private IReadOnlyList<IReadOnlyList<RoomBox>> Load(uint territory)
    {
        try
        {
            var row = _data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRowOrDefault(territory);
            var bg = row?.Bg.ExtractText() ?? string.Empty;
            var cut = bg.LastIndexOf('/');
            if (cut <= 0)
                return Array.Empty<IReadOnlyList<RoomBox>>();

            var file = _data.GetFile<LgbFile>($"bg/{bg[..cut]}/planmap.lgb");
            if (file == null)
                return Array.Empty<IReadOnlyList<RoomBox>>();

            var layouts = new List<IReadOnlyList<RoomBox>>();
            foreach (var layer in file.Layers)
            {
                var boxes = new List<RoomBox>();
                foreach (var obj in layer.InstanceObjects)
                {
                    if (obj.AssetType != LayerEntryType.EventRange
                        || obj.Object is not LayerCommon.EventRangeInstanceObject range)
                        continue;

                    // Priority 100 + slot. Rooms AND their hallways share a slot's priority, and all of
                    // them are kept: a trap in a corridor is still in that room's part of the floor.
                    var slot = range.ParentData.Priority - 100;
                    if (slot is < 0 or > 24)
                        continue;

                    var t = obj.Transform;
                    boxes.Add(new RoomBox((int)slot, t.Translation.X, t.Translation.Z, t.Scale.X, t.Scale.Z));
                }

                if (boxes.Select(b => b.Slot).Distinct().Count() >= FloorRooms.MinSlotsForLayout)
                    layouts.Add(boxes);
            }

            return layouts;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "DD geometry: could not read the layout for territory {0}", territory);
            return Array.Empty<IReadOnlyList<RoomBox>>();
        }
    }
}
