using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace Charon.Services.Game;

/// <summary>
/// Sends an in-game party invite through the native call the game's own UI uses
/// (<c>InfoProxyPartyInvite.InviteToParty</c>). Migrated from Daedalus's LAN window — the
/// content id + home world id come from the LAN roster (heartbeat data), so multi-word names
/// and cross-world (same data center) targets both work. Fail-open: returns false with a
/// reason, never throws. Framework thread only.
/// </summary>
public static unsafe class PartyInviteHelper
{
    public static bool TryInvite(string characterName, ulong contentId, ushort homeWorldId, out string detail)
    {
        characterName = characterName?.Trim() ?? "";
        if (characterName.Length == 0)
        {
            detail = "empty name";
            return false;
        }

        if (homeWorldId == 0)
        {
            detail = $"{characterName}: no world id yet (Daedalus heartbeat missing/old)";
            return false;
        }

        try
        {
            var proxy = InfoProxyPartyInvite.Instance();
            if (proxy == null)
            {
                detail = "invite proxy unavailable";
                return false;
            }

            var nameBytes = new byte[Encoding.UTF8.GetByteCount(characterName) + 1];
            Encoding.UTF8.GetBytes(characterName, 0, characterName.Length, nameBytes, 0);

            fixed (byte* namePtr = nameBytes)
            {
                proxy->InviteToParty(contentId, namePtr, homeWorldId);
            }

            detail = $"invite sent to {characterName}";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"threw {ex.GetType().Name}";
            return false;
        }
    }
}
