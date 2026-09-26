using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.Retainers;

namespace Charon.Services.Game;

/// <summary>
/// Everything the two retainer surfaces need to answer "what does this retainer run next": the per-retainer
/// assignment mode and picked venture out of the config, the catalog, and the price lookup.
///
/// Both windows share this so a plan cannot mean one thing in the board and another in the bell overlay, and
/// so the price cache is fetched once for them both instead of twice per frame.
/// </summary>
public sealed class RetainerPlanner
{
    private readonly CharonConfig _config;
    private readonly VentureSheetReader _sheet;
    private readonly Func<uint, long> _price;
    private readonly Action _save;

    private readonly Dictionary<uint, long> _prices = new();
    private DateTime _pricesUtc = DateTime.MinValue;

    public RetainerPlanner(CharonConfig config, VentureSheetReader sheet, Func<uint, long> price, Action save)
    {
        _config = config;
        _sheet = sheet;
        _price = price;
        _save = save;
    }

    public IReadOnlyList<VentureDef> Ventures => _sheet.Ventures;

    /// <summary>The sheet reader's status line, for the Debug page: it says whether the catalog is real.</summary>
    public string Status => _sheet.Status;

    /// <summary>An item's display name, for anywhere an item id has to be shown to a person.</summary>
    public string ItemName(uint itemId) => _sheet.ItemName(itemId);

    /// <summary>
    /// Prices per item, rebuilt at most once a minute. A draw asks several times (one per row, plus the open
    /// picker, plus the farm tab) and a market lookup per call would be silly.
    /// </summary>
    public IReadOnlyDictionary<uint, long> Prices()
    {
        if (DateTime.UtcNow - _pricesUtc < TimeSpan.FromMinutes(1))
            return _prices;

        _prices.Clear();
        foreach (var venture in _sheet.Ventures)
        {
            if (venture.ItemId != 0)
                _prices[venture.ItemId] = _price(venture.ItemId);
        }

        _pricesUtc = DateTime.UtcNow;
        return _prices;
    }

    /// <summary>
    /// Retainer settings are keyed by character AND retainer name: names repeat across a fleet, and the
    /// config file is per machine, so a bare name would have two boxes overwriting each other's plan.
    /// </summary>
    public string Key(ulong contentId, string retainer) => $"{contentId}:{retainer}";

    public VentureAssignment Mode(string key) =>
        _config.RetainerAssignment.TryGetValue(key, out var raw)
            ? (VentureAssignment)Math.Clamp(raw, 0, 3)
            : DefaultMode;

    public VentureAssignment DefaultMode => (VentureAssignment)Math.Clamp(_config.RetainerDefaultAssignment, 0, 3);

    public void SetMode(string key, VentureAssignment mode)
    {
        _config.RetainerAssignment[key] = (int)mode;
        _save();
    }

    public void SetDefaultMode(VentureAssignment mode)
    {
        _config.RetainerDefaultAssignment = (int)mode;
        _save();
    }

    public uint Picked(string key) =>
        _config.RetainerPickedVenture.TryGetValue(key, out var id) ? id : 0;

    public void SetPicked(string key, uint taskId)
    {
        if (taskId == 0)
            _config.RetainerPickedVenture.Remove(key);
        else
            _config.RetainerPickedVenture[key] = taskId;

        _save();
    }

    /// <summary>
    /// The retainer as the catalog sees it. Gear stats (item level, gathering) are not read yet, so the
    /// tier is an assumption the UI reports rather than a number it invents.
    /// </summary>
    public RetainerProfile Profile(RetainerVenture row) =>
        new(row.Name, _sheet.JobAbbreviation(row.JobId), row.Level, 0, 0);

    public List<FarmTarget> FarmTargets()
    {
        var targets = new List<FarmTarget>();
        foreach (var line in _config.RetainerFarmList)
        {
            if (!FarmTarget.TryParse(line, out var target))
                continue;

            if (target.ItemId == 0 && target.Name.Length > 0)
                target = target with { ItemId = ItemIdFor(target.Name) };

            targets.Add(target);
        }

        return targets;
    }

    public bool AddFarmTarget(string line)
    {
        if (line.Trim().Length == 0)
            return false;

        _config.RetainerFarmList.Add(line.Trim());
        _save();
        return true;
    }

    public void RemoveFarmTarget(FarmTarget target)
    {
        var line = _config.RetainerFarmList.FirstOrDefault(l =>
            FarmTarget.TryParse(l, out var parsed) &&
            parsed.ItemId == target.ItemId &&
            parsed.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));

        if (line != null)
        {
            _config.RetainerFarmList.Remove(line);
            _save();
        }
    }

    public VentureOption? Resolve(RetainerVenture row, string key) =>
        VenturePlanner.Resolve(Profile(row), Mode(key), Picked(key), FarmTargets(), _sheet.Ventures, Prices());

    /// <summary>
    /// The venture the next send should choose: whatever the mode resolves to (best value, the picked one,
    /// or the farm list), and 0 when the mode is Off or nothing qualifies — which means "send out" falls
    /// back to the runner's old behaviour rather than inventing a venture.
    /// </summary>
    public uint PlanTaskId(RetainerVenture? row, string key) =>
        row == null ? 0 : Resolve(row, key)?.Venture.TaskId ?? 0;

    public List<FarmPlan> PlanFarm(IReadOnlyList<RetainerVenture> rows) =>
        VenturePlanner.PlanFarm(
            FarmTargets(),
            rows.Select(Profile).ToList(),
            _sheet.Ventures,
            Prices());

    /// <summary>Resolve a farm-list name to an item id through the ventures we know about.</summary>
    public uint ItemIdFor(string name)
    {
        foreach (var venture in _sheet.Ventures)
        {
            if (venture.ItemId != 0 && venture.ItemName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return venture.ItemId;
        }

        return 0;
    }
}
