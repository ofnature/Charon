using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace Charon.Services.Game;

/// <summary>
/// Sends an in-game party invite through the native call the game's own UI uses
/// (<c>InfoProxyPartyInvite.InviteToParty</c>). Migrated from Daedalus's LAN window — the
/// content id + home world id come from the LAN roster (heartbeat data), so multi-word names
/// and cross-world (same data center) targets both work. Fail-open: returns false with a
/// reason, never throws. Framework thread only.
///
/// INSIDE AN INSTANCE (Eureka, a duty) that call does nothing: seven invites "sent" in Eureka Anemos
/// on 2026-11-16 and not one arrived, while invites by hand from the same spot did. The game has a
/// second call for that case, <c>InviteToPartyInInstanceByContentId</c>, which names the target by
/// content id alone. It is used whenever the client says it is in an instance area or bound by a
/// duty; if the game turns it down the ordinary call is still tried, and the detail says which went.
/// </summary>
public static unsafe class PartyInviteHelper
{
    /// <summary>The game's own answer to "is this an instance", for the caller to add duty state to.</summary>
    public static bool InInstanceArea()
    {
        var game = GameMain.Instance();
        return game != null && game->IsInInstanceArea();
    }

    public static bool TryInvite(string characterName, ulong contentId, ushort homeWorldId, bool inInstance,
        out string detail)
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

            var instanceNote = string.Empty;
            if (inInstance)
            {
                if (contentId == 0)
                    instanceNote = " (no content id for the in-instance invite)";
                else if (proxy->InviteToPartyInInstanceByContentId(contentId))
                {
                    detail = $"in-instance invite sent to {characterName}";
                    return true;
                }
                else
                    instanceNote = " (the game turned the in-instance invite down)";
            }

            var nameBytes = new byte[Encoding.UTF8.GetByteCount(characterName) + 1];
            Encoding.UTF8.GetBytes(characterName, 0, characterName.Length, nameBytes, 0);

            fixed (byte* namePtr = nameBytes)
            {
                // What the call returns has never been checked against an invite arriving, so it is
                // reported, not acted on.
                var answer = proxy->InviteToParty(contentId, namePtr, homeWorldId);
                detail = $"invite sent to {characterName}{instanceNote} (game answered {answer})";
            }

            return true;
        }
        catch (Exception ex)
        {
            detail = $"threw {ex.GetType().Name}";
            return false;
        }
    }
}
