// Ported from Character Panel Refined by Kouzukii - https://github.com/Kouzukii/ffxiv-characterstatus-refined
// (commit 0e4b68c, v1.8.6.0). MIT License, Copyright (c) 2022 Kouzukii - the full notice is in
// Charon/Features/CharacterPanel/LICENSE-CharacterPanelRefined.txt.
// Kept close to the original, formatting included, so a later upstream change can be diffed in.
// Changed for Charon: the four Debug log lines were dropped (they ran on every window update).
// The director offsets below (0x1F8E, 0x4D4, 0x4D6) are the original's and are not named in ClientStructs.
using System;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Charon.Services.Game.CharacterPanel;

public enum IlvlSyncType {
    Strict,
    // a lvl 80 535 weapon will not be synced in a level 80 duty
    // a higher level weapon will be synced to 530 however
    LevelBased
}

public static class IlvlSync {
    // E8 ?? ?? ?? ??  80 A3 ?? ?? ?? ?? EF  0F B6 83 ?? ?? ?? ??
    public static unsafe (uint?, IlvlSyncType) GetCurrentIlvlSync() {
        var icDirector = EventFramework.Instance() != null ? EventFramework.Instance()->GetInstanceContentDirector() : null;
        if (icDirector != null) {
            if (icDirector->InstanceContentType != InstanceContentType.BeginnerTraining && (icDirector->ContentDirector.Director.ContentFlags & 1) == 0) {
                var icd = (IntPtr)icDirector;
                // min ilvl
                if (*(byte*)(icd + 0x1F8E) >= 0x80 && *(ushort*)(icd + 0x4D4) > 0) {
                    return (*(ushort*)(icd + 0x4D4), IlvlSyncType.Strict);
                }

                // duty is synced
                if (((*(byte*)(icd + 0x1F8E) & 0x40) == 0 || UIState.Instance()->PlayerState.IsLevelSynced) && *(ushort*)(icd + 0x4D6) > 0) {
                    return (*(ushort*)(icd + 0x4D6), IlvlSyncType.Strict);
                }
            }
        }

        var pcDirector = EventFramework.Instance() != null ? EventFramework.Instance()->GetPublicContentDirector() : null;
        if (pcDirector != null) {
            var pcd = (IntPtr)pcDirector;
            // eureka / bozja is synced
            if (*(ushort*)(pcd + 0x4D6) > 0) {
                return (*(ushort*)(pcd + 0x4D6), IlvlSyncType.Strict);
            }
        }

        if (UIState.Instance()->PlayerState.IsLevelSynced) {
            var syncedLevel = UIState.Instance()->PlayerState.CurrentLevel;
            var ilvl = (uint)(syncedLevel switch {
                100 => 790,
                >=93 => 660 + (syncedLevel - 93) * 3,
                >=91 => 650 + (syncedLevel - 91) * 5,
                90 => 660,
                >=83 => 530 + (syncedLevel - 83) * 3,
                >=81 => 520 + (syncedLevel - 81) * 5,
                80 => 530,
                >=73 => 400 + (syncedLevel - 73) * 3,
                >=71 => 390 + (syncedLevel - 71) * 5,
                70 => 400,
                >=63 => 270 + (syncedLevel - 63) * 3,
                >=61 => 260 + (syncedLevel - 61) * 5,
                60 => 270,
                >=53 => 130 + (syncedLevel - 53) * 3,
                >=51 => 120 + (syncedLevel - 51) * 5,
                50 => 130,
                _ => syncedLevel
            });
            return (ilvl, IlvlSyncType.LevelBased);
        }

        return (null, IlvlSyncType.Strict);
    }
}
