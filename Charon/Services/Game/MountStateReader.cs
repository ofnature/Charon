using System;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace Charon.Services.Game;

/// <summary>Point-in-time view of the local player's mount: id, capacity, and per-seat occupants.</summary>
/// <param name="MountId">Mount sheet row id.</param>
/// <param name="PassengerSeats">Invitable passenger seats (Lumina ExtraSeats): 7 on an 8-person
/// mount, 3 on a 4-person mount. The owner rides the implicit last spot.</param>
/// <param name="SeatOccupantEntityIds">Entity id per passenger seat, index 0 = seat 1; 0 = empty.</param>
public sealed record MountSnapshot(uint MountId, int PassengerSeats, uint[] SeatOccupantEntityIds);

/// <summary>
/// Reads the local player's mount state from ClientStructs + Lumina. Best-effort and fail-open:
/// any read problem yields "not mounted" rather than an exception — this runs every frame.
///
/// Occupancy comes from rider CHARACTERS, not the owner's MountContainer: a passenger is any
/// nearby character in <see cref="CharacterModes.RidingPillion"/> whose ModeParam is the seat
/// number (1-based, same numbering as /ridepillion and the Ride Pillion menu). The
/// MountContainer entity-id array proved stale in ClientStructs (it read back float garbage),
/// so it is deliberately not used. Riders are tied to OUR mount by proximity — passengers sit
/// at the mount's position, so a tight radius filters out riders of other mounts parked nearby.
/// </summary>
public sealed unsafe class MountStateReader
{
    /// <summary>Riders further than this from the owner cannot be on the owner's mount.</summary>
    private const float MaxRiderDistanceYalms = 8f;

    private readonly IObjectTable _objectTable;
    private readonly IDataManager _dataManager;

    public MountStateReader(IObjectTable objectTable, IDataManager dataManager)
    {
        _objectTable = objectTable;
        _dataManager = dataManager;
    }

    /// <summary>Null when not mounted (or state is unreadable, e.g. during zone transitions).</summary>
    public MountSnapshot? ReadLocalMount() => ReadMountOf(_objectTable.LocalPlayer);

    /// <summary>Mount snapshot of any player character (owner-side or a nearby owner we might ride).</summary>
    public MountSnapshot? ReadMountOf(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter? player)
    {
        try
        {
            if (player == null || player.Address == nint.Zero)
                return null;

            var character = (Character*)player.Address;
            var mountId = character->Mount.MountId;
            if (mountId == 0)
                return null;

            var passengerSeats = GetPassengerSeats(mountId);
            var occupants = ReadSeatOccupants(player.Position, player.EntityId, passengerSeats);
            return new MountSnapshot(mountId, passengerSeats, occupants);
        }
        catch
        {
            // Structs can be mid-initialization during zone transitions — treat as not mounted.
            return null;
        }
    }

    /// <summary>
    /// True when this player could board a mount right now: not riding pillion and not on a
    /// mount of their own. Fail-closed — unreadable state means "cannot board".
    /// </summary>
    public bool CanBoard(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
    {
        try
        {
            if (player.Address == nint.Zero)
                return false;

            var character = (Character*)player.Address;
            return character->Mode != CharacterModes.RidingPillion && character->Mount.MountId == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the local player is riding pillion (a passenger, not a mount owner).</summary>
    public bool IsLocalRidingPillion()
    {
        try
        {
            var player = _objectTable.LocalPlayer;
            if (player == null || player.Address == nint.Zero)
                return false;

            return ((Character*)player.Address)->Mode == CharacterModes.RidingPillion;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Passenger-seat count from the Lumina Mount sheet (ExtraSeats; 0 = solo mount).</summary>
    public int GetPassengerSeats(uint mountId)
    {
        try
        {
            var sheet = _dataManager.GetExcelSheet<Mount>();
            if (sheet == null || !sheet.TryGetRow(mountId, out var row))
                return 0;
            return row.ExtraSeats;
        }
        catch
        {
            return 0;
        }
    }

    private uint[] ReadSeatOccupants(Vector3 ownerPosition, uint ownerEntityId, int passengerSeats)
    {
        var occupants = new uint[Math.Max(passengerSeats, 0)];
        if (passengerSeats < 1)
            return occupants;

        foreach (var obj in _objectTable)
        {
            // Riders are always player characters; the Character* cast is only valid for them.
            if (obj is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter)
                continue;
            if (obj.Address == nint.Zero || obj.EntityId == 0 || obj.EntityId == ownerEntityId)
                continue;
            if (Vector3.Distance(ownerPosition, obj.Position) > MaxRiderDistanceYalms)
                continue;

            var character = (Character*)obj.Address;
            if (character->Mode != CharacterModes.RidingPillion)
                continue;

            // ModeParam is the 1-based pillion seat number while RidingPillion.
            int seat = character->ModeParam;
            if (seat >= 1 && seat <= passengerSeats)
                occupants[seat - 1] = obj.EntityId;
        }

        return occupants;
    }
}
