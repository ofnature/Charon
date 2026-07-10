using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace Charon.Services.Game;

/// <summary>
/// Boards a party member's multi-seat mount by calling the game's own
/// <c>BattleChara.RidePillion(seatIndex)</c> on the OWNER's character — the same code path the
/// "Ride Pillion → Mount Seat #N" context menu uses. The /ridepillion text command is NOT used:
/// it only accepts placeholders (&lt;t&gt;, &lt;2&gt;…), never character names, so command
/// strings built from names are silently ignored by the game.
///
/// Takes the 1-based seat number used everywhere else in Charon (menu/ModeParam numbering) and
/// converts to the native call's 0-BASED index — verified in testing: passing 1 unconverted
/// boarded Mount Seat #2. Only works while grouped with the owner; the call is a no-op
/// otherwise. Must be called from the framework thread.
/// </summary>
public static unsafe class PillionRideHelper
{
    public static bool TryRidePillion(IPlayerCharacter owner, int seatIndex)
    {
        if (owner.Address == nint.Zero || seatIndex < 1 || seatIndex > 7)
            return false;

        try
        {
            ((BattleChara*)owner.Address)->RidePillion((uint)(seatIndex - 1));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
