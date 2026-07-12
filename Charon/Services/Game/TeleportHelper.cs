using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Charon.Services.Game;

/// <summary>
/// Executes a teleport to an aetheryte via the game's Telepo module — the same path the
/// Teleport action uses. Caller is responsible for picking an UNLOCKED aetheryte
/// (Dalamud's IAetheryteList only contains attuned ones). Fail-open, framework thread only.
/// </summary>
public static unsafe class TeleportHelper
{
    public static bool TryTeleport(uint aetheryteId, byte subIndex)
    {
        if (aetheryteId == 0)
            return false;

        try
        {
            var telepo = Telepo.Instance();
            if (telepo == null)
                return false;

            return telepo->Teleport(aetheryteId, subIndex);
        }
        catch
        {
            return false;
        }
    }
}
