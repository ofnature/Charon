using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin.Services;
using Charon.Features.Retainers;
using Lumina.Excel.Sheets;

namespace Charon.Services.Game;

/// <summary>
/// The venture tree, read once from the game's own sheets: which ventures exist, what they return, what
/// gates them, and what they cost. Everything the catalog reasons about comes from here, so the ranking
/// can be pure and testable while the sheet shapes stay in one file.
///
/// VERIFIED sheet facts this rests on (XIVAPI schema, 1114 RetainerTask rows):
///   RetainerTask: ClassJobCategory, Experience, IsRandom, MaxTimemin, RequiredGathering,
///                 RequiredItemLevel, RetainerLevel, RetainerTaskParameter, Task, VentureCost
///   RetainerTaskNormal: Item, Quantity (five tiers)
///   RetainerTaskParameter: ItemLevelDoW, PerceptionDoL, PerceptionFSH (the tier ladder per job family)
///   MaxTimemin is a DURATION CLASS, not a clock: 60 = a hunt, 1080 = a field exploration.
///
/// Fail-open: a sheet that will not read yields an empty catalog and a status line, never an exception
/// in a UI path.
/// </summary>
public sealed class VentureSheetReader
{
    private const int HuntMinutes = 60;
    private const int ExplorationMinutes = 1080;

    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly Dictionary<uint, string> _jobAbbreviations = new();
    private readonly Dictionary<uint, string> _categoryTokens = new();
    private readonly Dictionary<uint, string> _itemNames = new();

    private IReadOnlyList<VentureDef>? _ventures;

    public VentureSheetReader(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log = log;
    }

    /// <summary>Human-readable state of the read, for the Debug page.</summary>
    public string Status { get; private set; } = "not read yet";

    /// <summary>Every venture the game defines. Built once, then cached for the session.</summary>
    public IReadOnlyList<VentureDef> Ventures => _ventures ??= Build();

    /// <summary>The job abbreviation for a ClassJob row id (MIN, BTN, FSH, WAR…), or the id as text.</summary>
    public string JobAbbreviation(uint jobId)
    {
        if (_jobAbbreviations.Count == 0)
            ReadJobs();

        return _jobAbbreviations.TryGetValue(jobId, out var abbreviation) ? abbreviation : $"job {jobId}";
    }

    /// <summary>Item name, cached; blank when the id is unknown so callers can fall back to an id.</summary>
    public string ItemName(uint itemId)
    {
        if (itemId == 0)
            return string.Empty;

        if (_itemNames.TryGetValue(itemId, out var cached))
            return cached;

        var name = string.Empty;
        try
        {
            var sheet = _data.GetExcelSheet<Item>();
            if (sheet != null && sheet.TryGetRow(itemId, out var item))
                name = item.Name.ToString();
        }
        catch (Exception ex)
        {
            _log.Verbose(ex, "Ventures: item {0} unreadable", itemId);
        }

        _itemNames[itemId] = name;
        return name;
    }

    private void ReadJobs()
    {
        try
        {
            var sheet = _data.GetExcelSheet<ClassJob>();
            if (sheet == null)
                return;

            foreach (var row in sheet)
            {
                var abbreviation = row.Abbreviation.ToString();
                if (!string.IsNullOrWhiteSpace(abbreviation))
                    _jobAbbreviations[row.RowId] = abbreviation;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Ventures: ClassJob sheet unreadable");
        }
    }

    private IReadOnlyList<VentureDef> Build()
    {
        var ventures = new List<VentureDef>();
        try
        {
            var tasks = _data.GetExcelSheet<RetainerTask>();
            if (tasks == null)
            {
                Status = "RetainerTask sheet unavailable";
                return ventures;
            }

            var normal = _data.GetExcelSheet<RetainerTaskNormal>();
            var parameter = _data.GetExcelSheet<RetainerTaskParameter>();
            var random = _data.GetExcelSheet<RetainerTaskRandom>();

            foreach (var row in tasks)
            {
                try
                {
                    var taskRow = NormalRow(normal, row);
                    var itemId = (uint)(taskRow?.Item.RowId ?? 0);
                    // Quantity is a fixed five-step byte ladder (verified against the sheet schema).
                    var quantities = (taskRow?.Quantity.ToArray() ?? Array.Empty<byte>())
                        .Select(q => (int)q).ToList();
                    var thresholds = Thresholds(parameter, row, out var usesItemLevel);
                    var isRandom = row.IsRandom;

                    var name = isRandom
                        ? RandomPoolName(random, row)
                        : ItemName(itemId);
                    if (name.Length == 0)
                        name = $"venture {row.RowId}";

                    ventures.Add(new VentureDef(
                        row.RowId,
                        name,
                        isRandom ? VentureKind.Exploration : VentureKind.Hunting,
                        isRandom ? 0u : itemId,
                        isRandom ? string.Empty : ItemName(itemId),
                        quantities,
                        thresholds,
                        usesItemLevel,
                        row.RetainerLevel,
                        row.RequiredItemLevel,
                        row.RequiredGathering,
                        CategoryTokens(row.ClassJobCategory.RowId),
                        row.MaxTimemin > 0 ? row.MaxTimemin : HuntMinutes,
                        row.VentureCost,
                        row.Experience,
                        isRandom));
                }
                catch (Exception ex)
                {
                    _log.Verbose(ex, "Ventures: task {0} skipped", row.RowId);
                }
            }

            Status = $"{ventures.Count} ventures, {ventures.Count(v => v.Kind == VentureKind.Hunting)} hunting";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Ventures: RetainerTask sheet unreadable");
            Status = "RetainerTask sheet unreadable";
        }

        _log.Info("Venture catalog: {0}", Status);
        return ventures;
    }

    private static RetainerTaskNormal? NormalRow(Lumina.Excel.ExcelSheet<RetainerTaskNormal>? sheet, RetainerTask task)
    {
        if (sheet == null || task.IsRandom)
            return null;

        return sheet.TryGetRow(task.Task.RowId, out var row) ? row : null;
    }

    private static string RandomPoolName(Lumina.Excel.ExcelSheet<RetainerTaskRandom>? sheet, RetainerTask task)
    {
        if (sheet == null)
            return string.Empty;

        return sheet.TryGetRow(task.Task.RowId, out var row) ? row.Name.ToString() : string.Empty;
    }

    /// <summary>
    /// The tier ladder that decides the quantity, chosen by job family: gatherers are gated on
    /// perception (DoL for MIN/BTN, FSH for fishers) and everyone else on item level.
    /// </summary>
    private static List<int> Thresholds(
        Lumina.Excel.ExcelSheet<RetainerTaskParameter>? sheet,
        RetainerTask task,
        out bool usesItemLevel)
    {
        usesItemLevel = true;
        if (sheet == null || !sheet.TryGetRow(task.RetainerTaskParameter.RowId, out var row))
            return new List<int>();

        var tokens = task.ClassJobCategory.RowId != 0 ? task.ClassJobCategory.RowId : 0;
        var isFisher = tokens != 0 && task.RetainerTaskParameter.RowId != 0 && task.RequiredGathering > 0 && row.PerceptionFSH.Count > 0;
        var usesPerception = task.RequiredGathering > 0;

        if (usesPerception)
        {
            usesItemLevel = false;

            // FSH has its own ladder; MIN/BTN share the DoL one. The retainer's own numbers decide which,
            // but the sheet only tells us the requirement, so both are offered and the caller's stat picks.
            var ladder = isFisher && row.PerceptionFSH.Count > 0
                ? row.PerceptionFSH.ToArray()
                : row.PerceptionDoL.ToArray();

            return ladder.Select(v => (int)v).ToList();
        }

        return row.ItemLevelDoW.ToArray().Select(v => (int)v).ToList();
    }

    /// <summary>
    /// The jobs a venture accepts, as a comma-joined abbreviation list ("MIN", "BTN", "WAR,PLD,…").
    /// ClassJobCategory is a row of booleans, so the names come from reflecting its own properties —
    /// done once per category and cached, never per frame.
    /// </summary>
    private string CategoryTokens(uint categoryId)
    {
        if (categoryId == 0)
            return string.Empty;

        if (_categoryTokens.TryGetValue(categoryId, out var cached))
            return cached;

        var tokens = string.Empty;
        try
        {
            if (_jobAbbreviations.Count == 0)
                ReadJobs();

            var sheet = _data.GetExcelSheet<ClassJobCategory>();
            if (sheet != null && sheet.TryGetRow(categoryId, out var row))
            {
                var flags = new List<string>();
                foreach (var property in row.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.PropertyType != typeof(bool) || property.Name is "RowId")
                        continue;

                    if (property.GetValue(row) is true)
                        flags.Add(property.Name);
                }

                tokens = string.Join(",", flags);
            }
        }
        catch (Exception ex)
        {
            _log.Verbose(ex, "Ventures: ClassJobCategory {0} unreadable", categoryId);
        }

        _categoryTokens[categoryId] = tokens;
        return tokens;
    }
}
