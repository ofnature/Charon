// Ported from Character Panel Refined by Kouzukii - https://github.com/Kouzukii/ffxiv-characterstatus-refined
// (commit 0e4b68c, v1.8.6.0). MIT License, Copyright (c) 2022 Kouzukii - the full notice is in
// Charon/Features/CharacterPanel/LICENSE-CharacterPanelRefined.txt.
// Changed for Charon: these are the game-reading halves of the original's Equations.cs; the arithmetic
// they feed stayed in Features/CharacterPanel/Equations.cs.
using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.CharacterPanel;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Charon.Services.Game.CharacterPanel;

internal static unsafe class GameReads {
    private static (uint Ilvl, int Dmg)? cachedIlvl;

    public static (double AvgDamage, double NormalDamage, double CritDamage, double AvgHeal, double NormalHeal, double CritHeal) CalcExpectedOutput(CharacterPanelService plugin, UIState* uiState, JobId jobId, double det, double critMult, double critRate, double dh, double ten, in LevelModifier lvlModifier, uint? ilvlSync, IlvlSyncType ilvlSyncType) {
        try {
            var lvl = uiState->PlayerState.CurrentLevel;
            var ap = uiState->PlayerState.Attributes[(int)(jobId.IsCaster() ? Attributes.AttackMagicPotency : Attributes.AttackPower)];
            // The equipped weapon's sheet values, as the original reads them: an unnamed block of the
            // inventory manager. ClientStructs has no field for it, so the offset is the original's.
            var inventoryExcelData = (ushort*)((IntPtr)InventoryManager.Instance() + 9360);
            var weaponBaseDamage = /* phys/magic damage */ inventoryExcelData[jobId.IsCaster() ? 21 : 20] + /* hq bonus */ inventoryExcelData[33];
            if (ilvlSync != null && ( /* equip lvl */ inventoryExcelData[39] > lvl || ilvlSyncType == IlvlSyncType.Strict)) {
                if (cachedIlvl?.Ilvl != ilvlSync)
                    cachedIlvl = (ilvlSync.Value, plugin.DataManager.GetExcelSheet<ItemLevel>().GetRow(ilvlSync.Value).PhysicalDamage);
                weaponBaseDamage = Math.Min(cachedIlvl.Value.Dmg, weaponBaseDamage);
            }

            return Equations.CalcExpectedOutput(lvl, ap, weaponBaseDamage, jobId, det, critMult, critRate, dh, ten, lvlModifier);
        } catch (Exception e) {
            plugin.Log.Warning(e, "Character panel: failed to calculate raw damage");
            return (0, 0, 0, 0, 0, 0);
        }
    }

    public static Dictionary<Attributes, int> EstimateBaseStats(CharacterPanelService plugin, UIState* uiState) {
        var fd = new Dictionary<Attributes, int>();
        if (plugin.ObjectTable.LocalPlayer is not { } player)
            return fd;
        var foodSheet = plugin.DataManager.GetExcelSheet<ItemFood>();
        foreach (var status in player.StatusList.Where(s => s.StatusId is 48 or 49)) {
            var hq = Math.DivRem(status.Param, 10000, out var foodId);
            if (foodSheet.GetRowOrDefault((uint)foodId) is not { } food)
                return fd;
            foreach (var bonus in food.Params) {
                var attribute = (Attributes)bonus.BaseParam.RowId;
                var currentStat = fd.GetValueOrDefault(attribute, uiState->PlayerState.Attributes[(int)attribute]);
                fd[attribute] = Equations.WithoutFoodBonus(currentStat, hq == 1 ? bonus.ValueHQ : bonus.Value, bonus.IsRelative,
                    hq == 1 ? bonus.MaxHQ : bonus.Max);
            }
        }

        return fd;
    }
}
